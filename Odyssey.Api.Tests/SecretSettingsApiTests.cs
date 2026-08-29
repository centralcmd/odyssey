using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Api.Tests.Infrastructure;
using Odyssey.Context;
using Odyssey.Context.Secrets;
using Odyssey.Dtos.Application;
using Odyssey.Dtos.Authorization;
using Xunit;

namespace Odyssey.Api.Tests;

/// <summary>
/// HTTP-level coverage for the encrypted secret settings infrastructure (issue #444 §16).
///
/// <para>
/// Every criterion runs against the registered test-only key <c>DiagnosticsSelfTest</c>. That key
/// exists for exactly this reason: with an empty registry these assertions would have nothing to write
/// to, the structural guards would assert over an empty collection, and the whole suite would pass
/// vacuously at merge — first biting inside the first follow-up, which is the
/// "the guard existed but never fired" shape the design set out to prevent.
/// </para>
///
/// <para>
/// The <c>Testing</c> environment the shared factory boots is not Production, so the test-only
/// descriptor is present; <see cref="SecretSettingsRegistryTests"/> covers the Production filter.
/// </para>
/// </summary>
public class SecretSettingsApiTests
{
    private const string Path = "/api/system-settings/secrets";
    private const string Key = SecretSettingKeys.DiagnosticsSelfTest;
    private const string KeyPath = Path + "/" + Key;
    private const string ActorUserId = "44444444-4444-4444-4444-444444444444";

    /// <summary>A value distinctive enough that finding it anywhere is unambiguous.</summary>
    private const string Sentinel = "sk-odyssey-sentinel-3f9a2c7b-do-not-log";

    private static readonly string[] ReadOnly = [PermissionClaims.SystemSettingsRead];

    private static readonly string[] ReadAndCountUpdate =
    [
        PermissionClaims.SystemSettingsRead,
        PermissionClaims.SystemSettingsUpdate,
    ];

    private static readonly string[] ReadAndSecurityUpdate =
    [
        PermissionClaims.SystemSettingsRead,
        PermissionClaims.SystemSettingsSecurityUpdate,
    ];

    private static readonly string[] SecurityUpdateOnly = [PermissionClaims.SystemSettingsSecurityUpdate];

    // ── Storage and protection (ACs 1–6) ────────────────────────────────────────────────────────

    /// <summary>
    /// AC 1. The plaintext appears in NO column of the stored row — asserted by reading the row
    /// directly and searching every column, not just the one we expect to hold ciphertext.
    /// </summary>
    [Fact]
    public async Task Put_StoresCiphertext_AndNoColumnOfTheRowContainsThePlaintext()
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await Put(client, Sentinel)).StatusCode);

        var row = await ReadRow(factory);
        Assert.NotNull(row);
        Assert.Equal(SystemSettingSecret.CurrentProtectionScheme, row!.ProtectionScheme);

        foreach (var column in new[] { row.Key, row.Ciphertext, row.ProtectionScheme, row.UpdatedBy ?? string.Empty })
        {
            Assert.DoesNotContain(Sentinel, column, StringComparison.Ordinal);
        }

        Assert.Equal(ActorUserId, row.UpdatedBy);
    }

    /// <summary>
    /// AC 2. Two writes of the identical plaintext produce DIFFERENT ciphertexts — Data Protection's
    /// per-payload key derivation and IV — and both unprotect to the original. A deterministic
    /// ciphertext would let anyone with database read access confirm a guessed credential by
    /// comparison.
    /// </summary>
    [Fact]
    public async Task Put_Twice_WithTheSameValue_ProducesDifferentCiphertextsThatBothDecrypt()
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        await Put(client, Sentinel);
        var first = (await ReadRow(factory))!.Ciphertext;

        await Put(client, Sentinel);
        var second = (await ReadRow(factory))!.Ciphertext;

        Assert.NotEqual(first, second);

        using var scope = factory.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        Assert.Equal(Sentinel, protector.Unprotect(Key, first));
        Assert.Equal(Sentinel, protector.Unprotect(Key, second));
    }

    /// <summary>
    /// AC 3. Ciphertext written under one key and copied into another key's row fails to unprotect —
    /// the per-key sub-purpose binding. This is the substitution path anyone with direct database
    /// write access would otherwise have.
    /// </summary>
    [Fact]
    public async Task Ciphertext_CopiedFromAnotherKeysRow_IsUnreadable()
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);

        using var scope = factory.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();

        // Protected under a DIFFERENT sub-purpose, then planted in this key's row.
        var foreign = protector.Protect("SomeOtherKey", Sentinel);
        await WriteRow(factory, foreign, SystemSettingSecret.CurrentProtectionScheme);

        Assert.Equal(SecretSettingState.Unreadable, await GetState(factory));
        Assert.Equal(SecretReadState.Unreadable, (await Read(factory)).State);
    }

    /// <summary>
    /// AC 4. An unrecognised <c>ProtectionScheme</c> reports <c>Unreadable</c> rather than being
    /// parsed — the forward-compatibility tag doing its job, so a row written by a future format is
    /// reported instead of misread.
    /// </summary>
    [Fact]
    public async Task ARowWithAnUnknownProtectionScheme_IsUnreadable()
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();
        await Put(client, Sentinel);

        var row = await ReadRow(factory);
        await WriteRow(factory, row!.Ciphertext, "dp-v99");

        Assert.Equal(SecretSettingState.Unreadable, await GetState(factory));
        Assert.Equal(SecretReadState.Unreadable, (await Read(factory)).State);
    }

    /// <summary>
    /// AC 5. The cap is a <c>[StringLength]</c> on the request DTO, so model validation rejects the
    /// over-length value before the service is reached — which is why the bound lives in the attribute
    /// and not in a validator (CLAUDE.md).
    /// </summary>
    [Fact]
    public async Task Put_AtTheCap_RoundTrips_AndOneOverIsRejected()
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        var atCap = new string('a', SecretSettingKeys.MaxPlaintextLength);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(client, atCap)).StatusCode);
        Assert.Equal(SecretSettingState.Set, await GetState(factory));

        var overCap = new string('a', SecretSettingKeys.MaxPlaintextLength + 1);
        var rejected = await Put(client, overCap);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        // The stored value is untouched: a rejected write never reaches the service.
        using var scope = factory.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        Assert.Equal(atCap, protector.Unprotect(Key, (await ReadRow(factory))!.Ciphertext));
    }

    /// <summary>
    /// AC 6 (the HTTP half). A 3-byte UTF-8 character is rejected by the printable-ASCII rule. This is
    /// the rule that makes the byte count equal the character count, and therefore what keeps a
    /// maximum-length value inside the ciphertext column — the silent-truncation path a
    /// control-characters-only rule would have hit. The real-engine half is in
    /// <c>Odyssey.IntegrationTests</c>.
    /// </summary>
    [Theory]
    [InlineData("héllo")]          // 2-byte
    [InlineData("naïve€")]    // 3-byte euro sign
    [InlineData("line\nbreak")]    // control character — CR/LF is header injection downstream
    [InlineData("tab\tstop")]
    public async Task Put_WithANonPrintableAsciiValue_IsRejected(string value)
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        var response = await Put(client, value);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SecretSettingState.NotSet, await GetState(factory));
    }

    /// <summary>
    /// The printable-ASCII rule <strong>at its exact boundaries</strong> (PR #450 test review). The
    /// theory above samples values well inside and well outside the range, which cannot tell a correct
    /// <c>c &lt; 0x20 || c &gt; 0x7E</c> from an off-by-one on either end — and an off-by-one here is not
    /// cosmetic: rejecting <c>~</c> or a space refuses credentials that legitimately contain them,
    /// while admitting DEL or <c>0x1F</c> re-opens the control-character path that makes CR/LF in an
    /// SMTP handshake or an HTTP header injection.
    ///
    /// <para>
    /// Written as one theory over both verdicts so the two ends cannot drift apart, matching how the
    /// <c>MaxLength</c> boundary is pinned exactly one test away.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(' ', true)]        // 0x20 — the first printable character
    [InlineData('~', true)]        // 0x7E — the last
    [InlineData('\u001F', false)]  // 0x1F — one below the floor
    [InlineData('\u007F', false)]  // 0x7F — DEL, one above the ceiling
    public async Task Put_AtThePrintableAsciiBoundaries_IsAcceptedOnlyInsideTheRange(char character, bool accepted)
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        // Padded, so a leading/trailing space is not simply trimmed away before the rule sees it and
        // the assertion turns into one about Trim() instead.
        var value = $"sk{character}odyssey";

        var response = await Put(client, value);

        if (accepted)
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(SecretSettingState.Set, await GetState(factory));

            using var scope = factory.Services.CreateScope();
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            Assert.Equal(value, protector.Unprotect(Key, (await ReadRow(factory))!.Ciphertext));
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(SecretSettingState.NotSet, await GetState(factory));
        }
    }

    /// <summary>
    /// Issue #445 AC 9, on the one key where the printable-ASCII rule can plausibly bite a real
    /// credential: <c>Email:Password</c> is a human-chosen password at a third-party relay and may
    /// legitimately contain a character outside <c>0x20</c>–<c>0x7E</c>.
    ///
    /// <para>
    /// The per-descriptor relaxation was available and was DECLINED — the rule is also what keeps CR/LF
    /// out of an SMTP handshake — so what the criterion requires instead is that the refusal is
    /// actionable: a <c>400</c> whose message NAMES the constraint rather than a bare rejection. (The
    /// entry field also names it as the value is typed, so the round trip is the backstop, not the
    /// first line.) The message must still not echo the value.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Put_ARelayPasswordWithANonAsciiCharacter_IsRefusedWithTheConstraintNamed()
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        // The shape a real credential takes: a Nordic character in a otherwise ordinary password.
        const string password = "rel\u00e5y-p\u00e5ssw0rd!";

        var response = await client.PutAsJsonAsync(
            Path + "/" + SecretSettingKeys.EmailPassword, new SecretSettingUpdate { Value = password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("printable ASCII", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(password, body, StringComparison.Ordinal);

        // …and no descriptor quietly took the relaxation on this key's behalf.
        Assert.Equal(
            SecretSettingState.NotSet,
            (await factory.CreateClient().GetFromJsonAsync<List<SecretSettingStatusDto>>(Path))!
                .Single(status => status.Key == SecretSettingKeys.EmailPassword).State);
    }

    // ── Authorization and transport (ACs 7–11) ──────────────────────────────────────────────────

    /// <summary>
    /// AC 7. Holding read and the ordinary update claim is not enough: both writes need
    /// <c>system-settings.security.update</c>.
    /// </summary>
    [Fact]
    public async Task Writes_WithoutTheSecurityUpdateClaim_AreForbidden()
    {
        await using var factory = new ApiFactory(ReadAndCountUpdate);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await Put(client, Sentinel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(KeyPath)).StatusCode);
    }

    /// <summary>AC 8. The status endpoint needs <c>system-settings.read</c>.</summary>
    [Fact]
    public async Task Get_WithoutTheReadClaim_IsForbidden()
    {
        await using var factory = new ApiFactory([PermissionClaims.AccountsRead]);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Path)).StatusCode);
    }

    /// <summary>
    /// AC 8 (the other half). <c>system-settings.read</c> sits on the CLASS, writes included, so a
    /// write-only caller cannot use which keys 403 versus which 404 as a probe without ever
    /// successfully reading the resource.
    /// </summary>
    [Fact]
    public async Task Writes_WithoutTheReadClaim_AreForbidden_EvenHoldingTheWriteClaim()
    {
        await using var factory = new ApiFactory(SecurityUpdateOnly);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await Put(client, Sentinel)).StatusCode);
    }

    /// <summary>
    /// AC 9 — the ordering that a first draft contradicted itself on. An UNKNOWN key is a <c>403</c>
    /// for a caller lacking the write claim and a <c>404</c> for one holding it, which proves
    /// authorization is evaluated before key resolution. It has to be: <c>RequiredClaim</c> is a
    /// per-descriptor field and therefore unknowable until after resolution.
    /// </summary>
    [Fact]
    public async Task Put_OnAnUnknownKey_Is403WithoutTheClaimAnd404WithIt()
    {
        const string unknown = Path + "/NoSuchSecret";

        await using var without = new ApiFactory(ReadAndCountUpdate);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await without.CreateClient().PutAsJsonAsync(unknown, new SecretSettingUpdate { Value = Sentinel }))
                .StatusCode);

        await using var with = new ApiFactory(ReadAndSecurityUpdate);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await with.CreateClient().PutAsJsonAsync(unknown, new SecretSettingUpdate { Value = Sentinel }))
                .StatusCode);
    }

    /// <summary>AC 9, for the delete side.</summary>
    [Fact]
    public async Task Delete_OnAnUnknownKey_Is403WithoutTheClaimAnd404WithIt()
    {
        const string unknown = Path + "/NoSuchSecret";

        await using var without = new ApiFactory(ReadAndCountUpdate);
        Assert.Equal(HttpStatusCode.Forbidden, (await without.CreateClient().DeleteAsync(unknown)).StatusCode);

        await using var with = new ApiFactory(ReadAndSecurityUpdate);
        Assert.Equal(HttpStatusCode.NotFound, (await with.CreateClient().DeleteAsync(unknown)).StatusCode);
    }

    /// <summary>
    /// AC 11. The writes carry an EXPLICIT rate-limit policy. <c>MapControllers()</c> attaches no
    /// group-level policy in this pipeline, so without one these endpoints would have none at all —
    /// the "inherits the existing rate limiting" assumption a first draft made was simply false.
    /// </summary>
    [Fact]
    public async Task Put_BeyondTheRateLimit_Returns429WithRetryAfter()
    {
        await using var factory = new ApiFactory(
            ReadAndSecurityUpdate,
            new Dictionary<string, string?>
            {
                ["RateLimiting:SecretWrite:PermitLimit"] = "2",
                ["RateLimiting:SecretWrite:WindowSeconds"] = "600",
            });

        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await Put(client, Sentinel)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Put(client, Sentinel)).StatusCode);

        var limited = await Put(client, Sentinel);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    // ── Non-disclosure (ACs 12–15) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// AC 12. No response body from any of the three endpoints contains the stored value, in any
    /// state. Asserted across all three with a sentinel rather than by inspecting the DTO's declared
    /// properties, so a value arriving through an undeclared JSON member would still be caught.
    /// </summary>
    [Fact]
    public async Task NoResponseBody_FromAnyEndpoint_ContainsTheStoredValue()
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        var put = await Put(client, Sentinel);
        Assert.DoesNotContain(Sentinel, await put.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var get = await client.GetAsync(Path);
        Assert.DoesNotContain(Sentinel, await get.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // …and in the Unreadable state too, where the server has touched the ciphertext to probe it.
        await WriteRow(factory, "not-decryptable", SystemSettingSecret.CurrentProtectionScheme);
        var degraded = await client.GetAsync(Path);
        Assert.DoesNotContain(Sentinel, await degraded.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var delete = await client.DeleteAsync(KeyPath);
        Assert.DoesNotContain(Sentinel, await delete.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 13 — the test that covers the residual the parallel descriptor type does NOT eliminate.
    /// Separating the types prevents a secret reaching the existing
    /// <c>{OldValue} -&gt; {NewValue}</c> audit loop, which is a real guarantee; it does not prevent a
    /// new leak authored inside the secret service itself. Only a log-capturing assertion over the
    /// whole write path can cover that, and it is a test rather than a type guarantee.
    /// </summary>
    [Fact]
    public async Task NoLogEntry_AtAnyLevel_ContainsTheStoredValue()
    {
        await using var factory = new LoggingApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        await Put(client, Sentinel);
        await client.GetAsync("/api/system-settings");
        await client.GetAsync(Path);
        await client.DeleteAsync(KeyPath);

        var offenders = factory.Logs.Entries
            .Where(entry => entry.Message.Contains(Sentinel, StringComparison.Ordinal)
                || (entry.Exception?.ToString().Contains(Sentinel, StringComparison.Ordinal) ?? false))
            .Select(entry => $"{entry.Level} {entry.Category}: {entry.Message}")
            .ToList();

        Assert.True(offenders.Count == 0, "The credential reached the log: " + string.Join(" | ", offenders));
    }

    /// <summary>
    /// AC 14. BOTH record types redact themselves. <c>SecretResult</c> matters more than the request
    /// DTO: it is the type consumers actually hold — in a catch block's exception context, in a
    /// debugging <c>LogDebug("{Result}", result)</c>, inside a mail sender — and it is a record, so
    /// without this it prints its members.
    /// </summary>
    [Fact]
    public void BothRecordTypes_OmitTheValueFromToString()
    {
        Assert.DoesNotContain(
            Sentinel, new SecretSettingUpdate { Value = Sentinel }.ToString(), StringComparison.Ordinal);

        Assert.DoesNotContain(Sentinel, SecretResult.Found(Sentinel).ToString(), StringComparison.Ordinal);

        // The state is still legible, or the redaction would make the type useless to debug with.
        Assert.Contains("Found", SecretResult.Found(Sentinel).ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 15. A rejection names the KEY and the RULE, never the submitted value or its length —
    /// stricter than the plaintext settings service, which does interpolate offending values.
    /// </summary>
    [Theory]
    [InlineData("   ")]
    [InlineData("badévalue")]
    public async Task BadRequestBodies_ContainNeitherTheValueNorItsLength(string value)
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        var response = await Put(client, value);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(value, body, StringComparison.Ordinal);

        // A blank value trims to nothing, which is a substring of everything — so the length assertion
        // is the one that carries here, and the raw-value one above is what carries for the other case.
        if (value.Trim().Length > 0)
        {
            Assert.DoesNotContain(value.Trim(), body, StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"{value.Length} character", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// AC 15, for the over-length case, where the rejection comes from model validation rather than
    /// the service. The declared cap may legitimately appear ("1024 characters or fewer"); the
    /// SUBMITTED length must not.
    /// </summary>
    [Fact]
    public async Task AnOverLengthRejection_DoesNotEchoTheValue()
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        var value = new string('z', SecretSettingKeys.MaxPlaintextLength + 7);
        var body = await (await Put(client, value)).Content.ReadAsStringAsync();

        Assert.DoesNotContain(value, body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            (SecretSettingKeys.MaxPlaintextLength + 7).ToString(System.Globalization.CultureInfo.InvariantCulture),
            body,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The validation message keys on the REQUEST DTO PROPERTY, not the setting key. These are per-key
    /// endpoints whose body has one property, so the settings page's key-based join does not apply and
    /// the row reads <c>ErrorFor("Value")</c>.
    /// </summary>
    [Fact]
    public async Task AValidationFailure_IsKeyedOnTheRequestProperty()
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        var problem = await (await Put(client, "badévalue")).Content.ReadFromJsonAsync<ProblemBody>();

        Assert.NotNull(problem?.Errors);
        Assert.Contains(nameof(SecretSettingUpdate.Value), problem!.Errors!.Keys, StringComparer.OrdinalIgnoreCase);
    }

    // ── Read states (ACs 17–22) ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC 17. An absent row is HEALTHY, not degraded — the same posture CLAUDE.md records for the
    /// plaintext store. The status endpoint returns <c>200</c> with every key unset, never a
    /// <c>503</c>.
    /// </summary>
    [Fact]
    public async Task WithNoRowsAtAll_TheStatusEndpointReturns200AndEveryKeyIsNotSet()
    {
        await using var factory = new ApiFactory(ReadOnly);
        var client = factory.CreateClient();

        var response = await client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var statuses = await response.Content.ReadFromJsonAsync<List<SecretSettingStatusDto>>();
        Assert.NotNull(statuses);
        Assert.All(statuses!, status => Assert.Equal(SecretSettingState.NotSet, status.State));
        Assert.Contains(statuses!, status => status.Key == Key);

        Assert.Equal(SecretReadState.NotSet, (await Read(factory)).State);
    }

    /// <summary>
    /// AC 18 — the collapse that must never happen. A corrupted ciphertext reports
    /// <c>Unreadable</c>, NOT <c>NotSet</c>, from both the reader and the status endpoint. Conflating
    /// them is what would let a consumer fall back to a configured value the administrator believed
    /// they had replaced.
    /// </summary>
    [Fact]
    public async Task ACorruptedRow_IsUnreadable_NotNotSet()
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();
        await Put(client, Sentinel);

        var row = await ReadRow(factory);
        await WriteRow(factory, Corrupt(row!.Ciphertext), SystemSettingSecret.CurrentProtectionScheme);

        Assert.Equal(SecretSettingState.Unreadable, await GetState(factory));

        var result = await Read(factory);
        Assert.Equal(SecretReadState.Unreadable, result.State);
        Assert.False(result.TryGetValue(out _));
    }

    /// <summary>A healthy row reports <c>Set</c> with the attribution triple the plaintext store also exposes.</summary>
    [Fact]
    public async Task AStoredRow_ReportsSetWithAttribution()
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();
        await factory.SeedActorUserAsync(displayName: "Ada Lovelace");

        await Put(client, Sentinel);

        var status = await GetStatus(factory);
        Assert.Equal(SecretSettingState.Set, status.State);
        Assert.Equal(ActorUserId, status.UpdatedBy);
        Assert.Equal("Ada Lovelace", status.UpdatedByDisplayName);
        Assert.NotNull(status.UpdatedAt);
    }

    /// <summary>AC 20. Clearing an <c>Unreadable</c> row succeeds and leaves the key reporting <c>NotSet</c>.</summary>
    [Fact]
    public async Task Delete_OnAnUnreadableRow_Succeeds_AndLeavesItNotSet()
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        await WriteRow(factory, "not-decryptable", SystemSettingSecret.CurrentProtectionScheme);
        Assert.Equal(SecretSettingState.Unreadable, await GetState(factory));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(KeyPath)).StatusCode);
        Assert.Equal(SecretSettingState.NotSet, await GetState(factory));
    }

    /// <summary>
    /// AC 21. Clearing an already-absent key is also <c>204</c>: the caller's intent ("this must not
    /// be set") is satisfied either way, and distinguishing them is a needless oracle.
    /// </summary>
    [Fact]
    public async Task Delete_OnAnAbsentKey_Is204()
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(KeyPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(KeyPath)).StatusCode);
    }

    /// <summary>
    /// AC 22 — the consumer-shaped test. The reader is resolved through <see cref="IServiceScopeFactory"/>
    /// from a singleton-rooted call site, which is the pattern <c>SmtpEmailSender</c> would use, and
    /// all three states are observed through it. Without this the seam could be perfectly correct and
    /// still unreachable from where the follow-ups actually live.
    /// </summary>
    [Fact]
    public async Task TheReader_IsReachableFromASingletonRootedCallSite_AndReportsAllThreeStates()
    {
        await using var factory = new ApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        // Resolved from the ROOT provider, as a singleton would — the seam FileAnalysisApiKeyHandler,
        // SmtpEmailSender, EmailRecipientHashKey and LegalPseudonymizer all use, none of which can hold
        // a scoped reader (issue #445).
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();

        async Task<SecretResult> ReadThroughScope()
        {
            using var scope = scopeFactory.CreateScope();
            var reader = scope.ServiceProvider.GetRequiredService<ISecretSettingsReader>();
            return await reader.GetAsync(Key);
        }

        Assert.Equal(SecretReadState.NotSet, (await ReadThroughScope()).State);

        await Put(client, Sentinel);
        var found = await ReadThroughScope();
        Assert.Equal(SecretReadState.Found, found.State);
        Assert.True(found.TryGetValue(out var plaintext));
        Assert.Equal(Sentinel, plaintext);

        await WriteRow(factory, "not-decryptable", SystemSettingSecret.CurrentProtectionScheme);
        Assert.Equal(SecretReadState.Unreadable, (await ReadThroughScope()).State);
    }

    // ── Audit (ACs 27–29) ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC 27. Exactly one audit entry per successful write, naming key, actor and action, and
    /// carrying neither old nor new value.
    /// </summary>
    [Fact]
    public async Task ASuccessfulPut_EmitsOneAuditEntry_WithNoValues()
    {
        await using var factory = new LoggingApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        await Put(client, Sentinel);

        var entries = AuditEntries(factory, "set");
        Assert.Single(entries);
        Assert.Contains(Key, entries[0], StringComparison.Ordinal);
        Assert.Contains(ActorUserId, entries[0], StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, entries[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// AC 28. Re-saving the SAME plaintext emits an entry too. The absence of a line must not be
    /// inferable, because that absence would be a plaintext equality oracle for anyone who can read
    /// the log but not the store — which is why there is no change detection at all.
    /// </summary>
    [Fact]
    public async Task ReSavingTheSameValue_StillEmitsAnAuditEntry()
    {
        await using var factory = new LoggingApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        await Put(client, Sentinel);
        await Put(client, Sentinel);

        Assert.Equal(2, AuditEntries(factory, "set").Count);
    }

    /// <summary>AC 29. A clear emits an entry with action <c>cleared</c>.</summary>
    [Fact]
    public async Task ADelete_EmitsAClearedAuditEntry()
    {
        await using var factory = new LoggingApiFactory(ReadAndSecurityUpdate);
        var client = factory.CreateClient();

        await Put(client, Sentinel);
        await client.DeleteAsync(KeyPath);

        var entries = AuditEntries(factory, "cleared");
        Assert.Single(entries);
        Assert.Contains(Key, entries[0], StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, entries[0], StringComparison.Ordinal);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> Put(HttpClient client, string value) =>
        client.PutAsJsonAsync(KeyPath, new SecretSettingUpdate { Value = value });

    private static async Task<SecretSettingStatusDto> GetStatus(OdysseyApiFactory factory)
    {
        var statuses = await factory.CreateClient().GetFromJsonAsync<List<SecretSettingStatusDto>>(Path);
        return statuses!.Single(status => status.Key == Key);
    }

    private static async Task<SecretSettingState> GetState(OdysseyApiFactory factory) =>
        (await GetStatus(factory)).State;

    private static async Task<SecretResult> Read(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ISecretSettingsReader>();
        return await reader.GetAsync(Key);
    }

    private static async Task<SystemSettingSecret?> ReadRow(OdysseyApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();
        return await context.SystemSettingSecrets.AsNoTracking().FirstOrDefaultAsync(row => row.Key == Key);
    }

    /// <summary>Plants a row directly, bypassing the API — the only way to reach the degraded states.</summary>
    private static async Task WriteRow(OdysseyApiFactory factory, string ciphertext, string scheme)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OdysseyContext>();

        var row = await context.SystemSettingSecrets.FirstOrDefaultAsync(candidate => candidate.Key == Key);
        if (row is null)
        {
            context.SystemSettingSecrets.Add(new SystemSettingSecret
            {
                Key = Key,
                Ciphertext = ciphertext,
                ProtectionScheme = scheme,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            row.Ciphertext = ciphertext;
            row.ProtectionScheme = scheme;
        }

        await context.SaveChangesAsync();
    }

    /// <summary>Flips one character of the payload, so it decodes but does not authenticate.</summary>
    private static string Corrupt(string ciphertext)
    {
        var builder = new StringBuilder(ciphertext);
        var middle = builder.Length / 2;
        builder[middle] = builder[middle] == 'A' ? 'B' : 'A';
        return builder.ToString();
    }

    private static List<string> AuditEntries(LoggingApiFactory factory, string action) =>
        factory.Logs.Entries
            .Where(entry => entry.Category.Contains("SecretSettingsService", StringComparison.Ordinal))
            .Select(entry => entry.Message)
            .Where(message => message.Contains($" {action} by ", StringComparison.Ordinal))
            .ToList();

    private sealed record ProblemBody(IReadOnlyDictionary<string, string[]>? Errors);

    private class ApiFactory(
        IReadOnlyCollection<string>? permissions, IReadOnlyDictionary<string, string?>? configuration = null)
        : OdysseyApiFactory(permissions, ActorUserId, configuration);

    private sealed class LoggingApiFactory(IReadOnlyCollection<string>? permissions) : ApiFactory(permissions)
    {
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(Logs));
        }
    }
}
