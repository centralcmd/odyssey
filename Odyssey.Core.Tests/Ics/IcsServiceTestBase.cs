using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Odyssey.Context;
using Odyssey.Core.FileStorage.Files;
using Odyssey.Core.Finance;
using Odyssey.Core.Journal;
using Odyssey.Core.Tests.FileStorage;
using Odyssey.Dtos;
using Odyssey.Testing.FileStorage;

namespace Odyssey.Core.Tests.Ics;

/// <summary>
/// The <c>.ics</c> import/export services (journal, task, calendar) at the service tier: the production registrations
/// over one EF InMemory database and a real temp file volume, so an inline file or photo is written and read back as a
/// production host does. Every limit is the shipped default unless a test sets one on <see cref="Limits"/>,
/// <see cref="JournalLimits"/> or <see cref="UploadLimits"/> — the HTTP surface of the same rules (claim gates, status
/// codes, headers, transport limits) stays in <c>Odyssey.Api.Tests</c>.
/// </summary>
public abstract class IcsServiceTestBase : FileStorageTestBase
{
    protected const string ActorUserId = "ics-actor-id";

    private FileStorageTestHost? host;

    protected SettableImportExportLimits Limits { get; } = new();

    protected SettableJournalLimits JournalLimits { get; } = new();

    protected SettableUploadLimits UploadLimits { get; } = new();

    /// <summary>The store "process" every helper runs in, started on first use.</summary>
    protected async Task<FileStorageTestHost> HostAsync() =>
        host ??= await NewFilesHostAsync(configure: services =>
        {
            services.AddSingleton<IImportExportLimitsLookup>(Limits);
            services.AddSingleton<IJournalLimitsLookup>(JournalLimits);
            services.AddSingleton<IUploadLimitsLookup>(UploadLimits);
            services.AddScoped(sp => new FileValidationService(sp.GetRequiredService<IUploadLimitsLookup>()));
            services.AddScoped<IContactLookup, ContactLookup>();
            services.AddScoped<IFileLookup, FileLookup>();
            services.AddScoped<IPhotoLookup, PhotoLookup>();
            services.AddScoped<PhotoMetadataService>();
            services.AddScoped<PhotoService>();
            services.AddScoped<JournalEntryService>();
            services.AddScoped<JournalEntryIcsService>();
            services.AddScoped<JournalTaskService>();
            services.AddScoped<TaskIcsService>();
            services.AddScoped<CalendarIcsService>();
        });

    protected async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = (await HostAsync()).Scope();
        return await action(scope.ServiceProvider);
    }

    protected async Task InScopeAsync(Func<IServiceProvider, Task> action)
    {
        await using var scope = (await HostAsync()).Scope();
        await action(scope.ServiceProvider);
    }

    protected Task<T> WithContextAsync<T>(Func<OdysseyContext, Task<T>> action) =>
        InScopeAsync(sp => action(sp.GetRequiredService<OdysseyContext>()));

    protected Task WithContextAsync(Func<OdysseyContext, Task> action) =>
        InScopeAsync(sp => action(sp.GetRequiredService<OdysseyContext>()));

    // ------------------------------------------------------------------ seeds

    protected Task<Guid> SeedJournalTagAsync(string name, bool archived = false) => WithContextAsync(async context =>
    {
        var id = Guid.NewGuid();
        context.JournalTags.Add(new JournalTag { JournalTagId = id, Name = name, Archived = archived ? DateTime.UtcNow : null });
        await context.SaveChangesAsync();
        return id;
    });

    protected Task<List<string>> SeedManyJournalTagsAsync(int count) => WithContextAsync(async context =>
    {
        var names = Enumerable.Range(0, count).Select(i => $"captag{i}").ToList();
        context.JournalTags.AddRange(names.Select(name => new JournalTag { JournalTagId = Guid.NewGuid(), Name = name }));
        await context.SaveChangesAsync();
        return names;
    });

    protected Task<Guid> SeedContactAsync(string name, string externalUid) => WithContextAsync(async context =>
    {
        var id = Guid.NewGuid();
        context.Contacts.Add(new Contact
        {
            ContactId = id,
            ExternalUid = externalUid,
            NormalizedName = name.Trim().ToLowerInvariant(),
            Type = ContactType.Organization,
            OrganizationDetails = new() { LegalName = name },
        });
        await context.SaveChangesAsync();
        return id;
    });

    /// <summary>A file with real content and its real hash, so an import can match it.</summary>
    protected Task<Guid> SeedFileWithContentAsync(
        string fileName, string contentType, byte[] content, StoredFileArea area = StoredFileArea.Finance) =>
        InScopeAsync(async sp =>
            (await StoredFileFixtures.SeedFileAsync(sp, content, fileName, contentType, area, uploadedByUserId: ActorUserId)).Id);

    /// <summary>A file whose recorded hash matches no content, for a test that only links it.</summary>
    protected Task<Guid> SeedFileAsync(string fileName, string contentType) => InScopeAsync(async sp =>
    {
        var file = await StoredFileFixtures.StageFileAsync(sp, [1, 2, 3], fileName, contentType, uploadedByUserId: ActorUserId);
        file.Sha256Hash = Guid.NewGuid().ToString("N");
        await sp.GetRequiredService<OdysseyContext>().SaveChangesAsync();
        return file.Id;
    });

    protected Task<Guid> SeedPhotoAsync(string fileName = "photo.jpg", byte[]? bytes = null, string? title = null, string? caption = null) =>
        InScopeAsync(async sp =>
        {
            var photo = await StoredFileFixtures.StagePhotoAsync(sp, bytes ?? PhotoTestData.Jpeg(PhotoTestData.NextSeed()), fileName);
            photo.Title = title;
            photo.Caption = caption;
            await sp.GetRequiredService<OdysseyContext>().SaveChangesAsync();
            return photo.PhotoId;
        });

    /// <summary>A photo with the Files-store file it wrapped before the move still present (issue #428).</summary>
    protected Task<(Guid PhotoId, Guid FormerFileId)> SeedPhotoWithFormerFileAsync(string fileName) => InScopeAsync(async sp =>
    {
        var bytes = PhotoTestData.Jpeg(PhotoTestData.NextSeed());
        var file = await StoredFileFixtures.StageFileAsync(sp, bytes, fileName, "image/jpeg", StoredFileArea.Journal);
        var photo = await StoredFileFixtures.StagePhotoAsync(sp, bytes, fileName);
        photo.LegacyFileId = file.Id;
        await sp.GetRequiredService<OdysseyContext>().SaveChangesAsync();
        return (photo.PhotoId, file.Id);
    });

    protected Task<List<FileMetadata>> FilesAsync() => WithContextAsync(c => c.FileMetadata.AsNoTracking().ToListAsync());

    protected Task<List<Photo>> PhotosAsync() => WithContextAsync(c => c.Photos.AsNoTracking().ToListAsync());

    // ------------------------------------------------------------------ text helpers

    protected static MemoryStream Utf8(string text) => new(Encoding.UTF8.GetBytes(text));

    /// <summary>RFC 5545 unfolding: a CRLF followed by one space or tab is removed.</summary>
    protected static string Unfold(string ics) => ics.Replace("\r\n ", string.Empty).Replace("\r\n\t", string.Empty);

    protected static int Count(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    protected static string Sha256(byte[] content) => PhotoTestData.Sha256(content);

    /// <summary>One inline attachment line, folded the way a producer might: base64 split over continuation lines.</summary>
    protected static string InlineAttach(byte[] content, string fileName, string contentType, string? extraParams = null)
    {
        var base64 = Convert.ToBase64String(content);
        var folded = string.Join("\r\n ", Enumerable.Range(0, (base64.Length + 59) / 60).Select(i => base64.Substring(i * 60, Math.Min(60, base64.Length - i * 60))));
        return $"ATTACH;FMTTYPE={contentType};ENCODING=BASE64;VALUE=BINARY;FILENAME=\"{fileName}\"{extraParams}:{folded}";
    }

    protected static string InlinePhoto(byte[] content, string fileName, string? title = null, string? caption = null) =>
        InlineAttach(content, fileName, "image/jpeg",
            ";X-ODYSSEY-KIND=PHOTO"
            + (title is null ? "" : $";X-ODYSSEY-TITLE=\"{title}\"")
            + (caption is null ? "" : $";X-ODYSSEY-CAPTION=\"{caption}\""));

    // ------------------------------------------------------------------ settable lookups

    /// <summary>The import/export caps, the shipped defaults unless a test replaces <see cref="Value"/>.</summary>
    public sealed class SettableImportExportLimits : IImportExportLimitsLookup
    {
        private const long Megabyte = 1024L * 1024;

        public ImportExportLimits Value { get; set; } = new(
            ContactVCardMaxExportRows: null,
            ContactVCardMaxImportEntries: null,
            ContactVCardMaxImportBytes: SystemSettingsDefaults.ContactVCardMaxImportMegabytes * Megabyte,
            ContactVCardMaxExportBytes: SystemSettingsDefaults.ContactVCardMaxExportMegabytes * Megabyte,
            CalendarIcsMaxExportEvents: null,
            CalendarIcsMaxImportEvents: null,
            CalendarIcsMaxImportBytes: SystemSettingsDefaults.CalendarIcsMaxImportMegabytes * Megabyte,
            CalendarIcsMaxExportBytes: SystemSettingsDefaults.CalendarIcsMaxExportMegabytes * Megabyte,
            TaskIcsMaxExportTasks: null,
            TaskIcsMaxImportTasks: null,
            TaskIcsMaxImportBytes: SystemSettingsDefaults.TaskIcsMaxImportMegabytes * Megabyte,
            TaskIcsMaxExportBytes: SystemSettingsDefaults.TaskIcsMaxExportMegabytes * Megabyte,
            JournalIcsMaxExportRows: null,
            JournalIcsMaxImportEntries: null,
            JournalIcsMaxImportBytes: SystemSettingsDefaults.JournalIcsMaxImportMegabytes * Megabyte,
            JournalIcsMaxExportBytes: SystemSettingsDefaults.JournalIcsMaxExportMegabytes * Megabyte,
            CalendarIcsMaxAggregateExportRows: SystemSettingsDefaults.CalendarIcsMaxAggregateExportRows,
            CalendarIcsMaxAggregateOccurrences: SystemSettingsDefaults.CalendarIcsMaxAggregateOccurrences,
            CalendarIcsMaxAggregateExportWindowDays: SystemSettingsDefaults.CalendarIcsMaxAggregateExportWindowDays,
            ContactVCardMaxRepeatablePropertiesPerEntry: SystemSettingsDefaults.ContactVCardMaxRepeatablePropertiesPerEntry,
            ImportMaxSamplesPerSkipReason: SystemSettingsDefaults.ImportMaxSamplesPerSkipReason,
            NewsOpmlMaxImportBytes: SystemSettingsDefaults.NewsOpmlMaxImportMegabytes * Megabyte,
            IsDegraded: false);

        public Task<ImportExportLimits> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
    }

    /// <summary>The journal caps, the shipped defaults unless a test replaces <see cref="Value"/>; counts its reads.</summary>
    public sealed class SettableJournalLimits : IJournalLimitsLookup
    {
        public JournalLimits Value { get; set; } = new(
            PhotoMaxLinksPerKind: SystemSettingsDefaults.PhotoMaxLinksPerKind,
            PhotoMaxAlbumMembers: SystemSettingsDefaults.PhotoMaxAlbumMembers,
            JournalEntryMaxLinksPerKind: SystemSettingsDefaults.JournalEntryMaxLinksPerKind,
            JournalTaskMaxLinksPerKind: SystemSettingsDefaults.JournalTaskMaxLinksPerKind,
            PhotoMetadataReadBytes: SystemSettingsDefaults.PhotoMetadataReadMegabytes * 1024L * 1024,
            PhotoMetadataExtractionTimeoutSeconds: SystemSettingsDefaults.PhotoMetadataExtractionTimeoutSeconds,
            CalendarMaxWindowDays: SystemSettingsDefaults.CalendarMaxWindowDays,
            CalendarMaxEventDurationDays: SystemSettingsDefaults.CalendarMaxEventDurationDays,
            RecurrenceMaxGeneratedOccurrences: SystemSettingsDefaults.RecurrenceMaxGeneratedOccurrences,
            IsDegraded: false);

        public int Calls { get; private set; }

        public Task<JournalLimits> GetAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Value);
        }
    }

    /// <summary>The admin upload cap, the shipped default unless a test sets <see cref="Megabytes"/>.</summary>
    public sealed class SettableUploadLimits : IUploadLimitsLookup
    {
        public int Megabytes { get; set; } = SystemSettingsDefaults.FileStorageMaxUploadMegabytes;

        public Task<UploadLimits> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new UploadLimits(Megabytes * 1024L * 1024, Megabytes, IsDegraded: false));
    }
}
