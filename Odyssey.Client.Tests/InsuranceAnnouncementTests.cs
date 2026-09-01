using Odyssey.Client.Pages.Finance;
using Odyssey.Dtos.Finance;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// What the insurance page announces after a write (issue #26 §3).
///
/// <para>
/// Both announcements exist because the state they describe is otherwise silent: a document count
/// lives on an unfocused chip's <c>aria-label</c>, and the enable transition changes a menu item the
/// user is not in. A live region that stops firing therefore breaks nothing visible, which is exactly
/// why the conditions are pinned here rather than left inside the card.
/// </para>
/// </summary>
public class InsuranceAnnouncementTests
{
    private static ExistingPolicyRenewal Period(params string[] fileNames) => new()
    {
        PolicyRenewalId = Guid.NewGuid(),
        InsurancePolicyId = Guid.NewGuid(),
        FromDate = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        ToDate = new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc),
        Premium = 1m,
        PremiumCurrencyCode = "USD",
        CoverageAmount = 1m,
        CoverageCurrencyCode = "USD",
        CreatedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Files = [.. fileNames.Select(name => new ExistingPolicyRenewalFile
        {
            Id = Guid.NewGuid(),
            PolicyRenewalId = Guid.NewGuid(),
            FileMetadata = new ExistingFileMetadata
            {
                Id = Guid.NewGuid(),
                FileName = name,
                ContentType = "application/pdf",
                SizeBytes = 1,
                FileBlobId = Guid.NewGuid(),
                UploadedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            AttachedAtUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        })],
    };

    /// <summary>
    /// The period is NAMED, not called "this period": the row menu may have inferred the target and
    /// the dialog's picker may have changed it, so a user who cannot see the record has no other way
    /// to know where the documents went.
    /// </summary>
    [Fact]
    public void The_attach_announcement_names_the_period_it_counts()
    {
        var message = InsuranceAnnouncements.DocumentsOnPeriod(Period("a.pdf", "b.pdf"));

        Assert.Equal("2 documents on Mar 01, 2025 → Feb 28, 2026.", message);
    }

    [Fact]
    public void One_document_is_singular()
    {
        Assert.StartsWith("1 document on", InsuranceAnnouncements.DocumentsOnPeriod(Period("only.pdf")));
    }

    /// <summary>Reachable by detaching the last document, so the count has to read as a count.</summary>
    [Fact]
    public void Zero_documents_is_plural()
    {
        Assert.StartsWith("0 documents on", InsuranceAnnouncements.DocumentsOnPeriod(Period()));
    }

    /// <summary>The transition the row menu's gate depends on: nothing to attach to, then something.</summary>
    [Fact]
    public void The_first_period_announces_that_attaching_is_now_possible()
    {
        var message = InsuranceAnnouncements.PeriodsBecameAvailable(0, 1);

        Assert.NotNull(message);
        Assert.Contains("Attach document", message);
    }

    /// <summary>
    /// A later period changes nothing about the gate. Announcing it anyway is noise that trains a
    /// screen-reader user to ignore the region, which costs them the announcements that matter.
    /// </summary>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    public void A_later_period_announces_nothing(int before, int after)
    {
        Assert.Null(InsuranceAnnouncements.PeriodsBecameAvailable(before, after));
    }

    /// <summary>A failed save leaves the count where it was; there is no transition to announce.</summary>
    [Fact]
    public void An_unchanged_count_announces_nothing()
    {
        Assert.Null(InsuranceAnnouncements.PeriodsBecameAvailable(0, 0));
        Assert.Null(InsuranceAnnouncements.PeriodsBecameAvailable(2, 2));
    }

    /// <summary>
    /// Deleting the last period disables the action again — a transition, but not this one. It is
    /// announced by nothing today, which is a known gap rather than a silent one.
    /// </summary>
    [Fact]
    public void Losing_the_last_period_is_not_announced_as_a_gain()
    {
        Assert.Null(InsuranceAnnouncements.PeriodsBecameAvailable(1, 0));
    }
}
