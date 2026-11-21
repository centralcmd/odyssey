using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Odyssey.ApiClient.Resources;
using Odyssey.Client.Pages.Attachments;
using Odyssey.Client.Pages.Journal;
using Odyssey.Client.Services;
using Odyssey.Dtos.Authorization;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Issue #522 AC 6, made countable (test review on #529): the shared fields hand every announcement to the host's one
/// announcer through <see cref="AttachmentLinkFields.OnAnnounce"/>, so recording that callback shows each add, remove and
/// cap message is spoken exactly once — which reading a live region's current text cannot.
/// </summary>
public class AttachmentLinkFieldsAnnouncementTests
{
    static AttachmentLinkFieldsAnnouncementTests() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(10);

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(int? cap, int photos = 0, int files = 0)
        {
            Ctx = PageTestContext.Create(PermissionClaims.PhotosRead, PermissionClaims.PhotosCreate, PermissionClaims.FilesRead);
            Ctx.JSInterop.SetupModule("./js/focus-return.js");
            var upload = new Mock<IUploadLimitsCache>();
            upload.Setup(u => u.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(UploadLimitsCache.Fallback);
            Ctx.Services.AddSingleton(upload.Object);
            FilesApi.Setup(f => f.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => Meta(id, $"{id:N}.pdf"));
            Ctx.Services.AddSingleton(FilesApi.Object);
            Photos = [.. Enumerable.Range(1, photos).Select(i => new JournalDraftPhoto(Guid.NewGuid(), $"photo-{i}.jpg"))];
            Files = [.. Enumerable.Range(1, files).Select(i => Meta(Guid.NewGuid(), $"file-{i}.pdf"))];
            Cut = Ctx.Render<AttachmentLinkFields>(p => p
                .Add(f => f.Photos, Photos)
                .Add(f => f.Attachments, Files)
                .Add(f => f.Voice, AttachmentLinkVoice.Calendar)
                .Add(f => f.Noun, "event")
                .Add(f => f.Cap, cap)
                .Add(f => f.OnAnnounce, EventCallback.Factory.Create<string>(this, m => Announced.Add(m))));
        }

        public BunitContext Ctx { get; }

        public Mock<IFilesApiClient> FilesApi { get; } = new();

        public List<JournalDraftPhoto> Photos { get; }

        public List<FileMetadataResponse> Files { get; }

        public IRenderedComponent<AttachmentLinkFields> Cut { get; }

        public List<string> Announced { get; } = [];

        public async Task PickPhotosAsync(int count)
        {
            Cut.FindAll("button").Single(b => b.TextContent.Contains("Add photos", StringComparison.Ordinal)).Click();
            var picker = Cut.FindComponent<AttachPhotosDialog>();
            IReadOnlyList<AttachPhotoItem> items =
                [.. Enumerable.Range(1, count).Select(i => new AttachPhotoItem(AttachDocumentSource.Library, Guid.NewGuid(), $"picked-{i}.jpg"))];
            await Cut.InvokeAsync(() => picker.Instance.OnSubmit.InvokeAsync(items));
        }

        /// <summary>Hands <paramref name="count"/> files over as the document picker does: one Attach each, then OnAttached.</summary>
        public async Task PickFilesAsync(int count)
        {
            Cut.FindAll("button").Single(b => b.TextContent.Contains("Attach documents", StringComparison.Ordinal)).Click();
            var picker = Cut.FindComponent<AttachDocumentsDialog>().Instance;
            var items = Enumerable.Range(1, count)
                .Select(i => new AttachDocumentItem(AttachDocumentSource.Library, Guid.NewGuid(), $"doc-{i}.pdf", "Other", 10, null, null, null, null))
                .ToList();
            await Cut.InvokeAsync(async () =>
            {
                foreach (var item in items)
                {
                    await picker.Attach(item);
                }

                await picker.OnAttached.InvokeAsync(items);
            });
        }

        public ValueTask DisposeAsync() => Ctx.DisposeAsync();
    }

    private static FileMetadataResponse Meta(Guid id, string name) =>
        new(id, name, "application/pdf", 10, "hash", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), null);

    [Fact] // AC 6: a photo pick is announced exactly once
    public async Task A_photo_pick_is_announced_exactly_once()
    {
        await using var f = new Fixture(cap: 10);

        await f.PickPhotosAsync(2);

        Assert.Equal(["Added 2 photos."], f.Announced);
    }

    [Fact] // AC 6: a document pick of several files is one announcement, not one per file
    public async Task A_document_pick_is_announced_exactly_once()
    {
        await using var f = new Fixture(cap: 10);

        await f.PickFilesAsync(3);

        Assert.Equal(["Added 3 documents."], f.Announced);
        Assert.Equal(3, f.Files.Count);
    }

    [Fact] // AC 6: a removal is announced exactly once
    public async Task A_removal_is_announced_exactly_once()
    {
        await using var f = new Fixture(cap: 10, photos: 2);

        f.Cut.Find("button[aria-label='Remove photo-1.jpg']").Click();

        Assert.Equal(["Removed photo-1.jpg."], f.Announced);
    }

    [Fact] // AC 6 + §3.1: a pick over the cap is one cap message, never also an "Added" one
    public async Task A_pick_over_the_cap_is_announced_exactly_once()
    {
        await using var f = new Fixture(cap: 2, photos: 1);

        await f.PickPhotosAsync(3);

        Assert.Equal(["Added 1. Photo limit reached: 2."], f.Announced);
        Assert.Equal(2, f.Photos.Count);
    }

    [Fact] // AC 6 + §3.1: a document pick over the cap likewise
    public async Task A_document_pick_over_the_cap_is_announced_exactly_once()
    {
        await using var f = new Fixture(cap: 2, files: 1);

        await f.PickFilesAsync(3);

        Assert.Equal(["Added 1. Document limit reached: 2."], f.Announced);
        Assert.Equal(2, f.Files.Count);
    }
}
