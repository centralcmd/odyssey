using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Core.Finance;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;

namespace Odyssey.Core.Tests;

/// <summary>
/// Builds a <see cref="FileAnalysisService"/> together with the queue and the extraction runner that the worker
/// uses since issue #444, so a test can enqueue and then run a job in one call
/// (<see cref="AnalyzeAndRunAsync"/>). The runner re-checks the requester, so the requester is seeded as a user
/// holding <c>file-analysis.create</c> through a role.
/// </summary>
internal static class FileAnalysisTestHarness
{
    public const string RequesterId = "user-1";

    public static readonly IFileAnalysisPromptTemplate Template = new TestPromptTemplate();

    private static readonly ConditionalWeakTable<FileAnalysisService, FileAnalysisExtractionRunner> Runners = new();

    public static FileAnalysisJobQueue Queue(OdysseyContext context, FileAnalysisOptions? options = null, TimeProvider? time = null) =>
        new(context, Options.Create(options ?? new FileAnalysisOptions()), time ?? TimeProvider.System,
            NullLogger<FileAnalysisJobQueue>.Instance);

    public static FileAnalysisService Create(
        OdysseyContext context,
        IFileAnalysisProvider provider,
        IContactLookup contactLookup,
        FileAnalysisOptions options,
        IFileAnalysisSettingsLookup settingsLookup,
        ISecretSettingsReader? secrets = null,
        TimeProvider? time = null,
        IFileAnalysisPromptTemplate? template = null)
    {
        SeedRequester(context, RequesterId);
        var service = new FileAnalysisService(
            context, provider, contactLookup, Options.Create(options), settingsLookup,
            NullLogger<FileAnalysisService>.Instance, Queue(context, options, time), template ?? Template, time);
        Runners.AddOrUpdate(service, Runner(context, provider, settingsLookup, secrets, time, template));
        return service;
    }

    public static FileAnalysisExtractionRunner Runner(
        OdysseyContext context,
        IFileAnalysisProvider provider,
        IFileAnalysisSettingsLookup settingsLookup,
        ISecretSettingsReader? secrets = null,
        TimeProvider? time = null,
        IFileAnalysisPromptTemplate? template = null) =>
        new(context, provider, settingsLookup, template ?? Template, secrets ?? new KeyStored(),
            time ?? TimeProvider.System, NullLogger<FileAnalysisExtractionRunner>.Instance);

    /// <summary>Enqueues, then claims and runs the job in-process — what the worker does a tick later.</summary>
    public static async Task<AnalyzeFileResponse> AnalyzeAndRunAsync(
        this FileAnalysisService service, Guid accountId, Guid fileId, string requestedByUserId,
        AnalyzeFileRequest? consent = null, CancellationToken cancellationToken = default)
    {
        var response = await service.AnalyzeAsync(accountId, fileId, requestedByUserId, consent, cancellationToken);
        if (!Runners.TryGetValue(service, out var runner))
        {
            throw new InvalidOperationException("Build the service with FileAnalysisTestHarness.Create to run its jobs.");
        }

        await service.RunInlineAsync(response.AnalysisJobId, runner, cancellationToken);
        return response;
    }

    /// <summary>Seeds a user holding <c>file-analysis.create</c> through a role (idempotent per context).</summary>
    public static void SeedRequester(OdysseyContext context, string userId)
    {
        if (context.Users.AsNoTracking().Any(u => u.Id == userId))
        {
            return;
        }

        var roleId = $"role-{userId}";
        var user = new ApplicationUser { Id = userId, UserName = userId, NormalizedUserName = userId.ToUpperInvariant() };
        var role = new IdentityRole { Id = roleId, Name = roleId, NormalizedName = roleId.ToUpperInvariant() };
        var userRole = new IdentityUserRole<string> { UserId = userId, RoleId = roleId };
        var claim = new IdentityRoleClaim<string>
        {
            RoleId = roleId, ClaimType = PermissionClaims.Type, ClaimValue = PermissionClaims.FileAnalysisCreate,
        };
        context.Users.Add(user);
        context.Roles.Add(role);
        context.UserRoles.Add(userRole);
        context.RoleClaims.Add(claim);
        context.SaveChanges();

        // OdysseyContext locks every NEW user out at save time (admin approval); approve this one, as an
        // administrator would, in a second save.
        user.LockoutEnd = null;
        context.SaveChanges();

        // Detach only what was seeded here: a test may already hold tracked entities of its own.
        foreach (var entity in new object[] { user, role, userRole, claim })
        {
            context.Entry(entity).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }
    }

    /// <summary>The fixed test prompt; its hash is what an enqueued job is stamped with.</summary>
    internal sealed class TestPromptTemplate(string text = "Extract transactions.", string version = "1.1") : IFileAnalysisPromptTemplate
    {
        public string Version { get; } = version;

        public string Text { get; } = text;

        public string Sha256 => EmbeddedFileAnalysisPromptTemplate.HashOf(Text);
    }

    /// <summary>A credential store that answers <c>Found</c> for every key (or the result a test sets).</summary>
    internal sealed class KeyStored : ISecretSettingsReader
    {
        public SecretResult Result { get; set; } = SecretResult.Found("test-api-key");

        public Task<SecretResult> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }
}
