using Odyssey.Core;
using Odyssey.Core.Finance;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>
/// The shared document-validity rule (issue #146 §8.2) — the one definition both the account-file and
/// contract-file write paths route through. AC 9, 17 and 22 at their source; the four endpoints that
/// call it are covered separately.
/// </summary>
public class DocumentValidityTests
{
    [Fact]
    public void Normalize_UnspecifiedKind_IsStampedUtcWithoutShiftingTheClockReading()
    {
        var unspecified = new DateTime(2026, 1, 1, 9, 30, 0, DateTimeKind.Unspecified);

        var (validFrom, _, _) = DocumentValidity.Normalize(unspecified, null, null);

        Assert.Equal(DateTimeKind.Utc, validFrom!.Value.Kind);
        Assert.Equal(unspecified.TimeOfDay, validFrom.Value.TimeOfDay);
    }

    [Fact]
    public void Normalize_LocalKind_IsConvertedToTheSameInstantInUtc()
    {
        var local = new DateTime(2026, 1, 1, 9, 30, 0, DateTimeKind.Local);

        var (_, _, issuedAt) = DocumentValidity.Normalize(null, null, local);

        Assert.Equal(DateTimeKind.Utc, issuedAt!.Value.Kind);
        Assert.Equal(local.ToUniversalTime(), issuedAt.Value);
    }

    [Fact]
    public void Normalize_NullsStayNull()
    {
        var (validFrom, validTo, issuedAt) = DocumentValidity.Normalize(null, null, null);

        Assert.Null(validFrom);
        Assert.Null(validTo);
        Assert.Null(issuedAt);
    }

    [Fact]
    public void Normalize_ValidToBeforeValidFrom_ThrowsAttributedToValidTo()
    {
        var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var error = Assert.Throws<DomainValidationException>(
            () => DocumentValidity.Normalize(from, from.AddDays(-1), null));

        Assert.Equal(400, error.StatusCode);
        Assert.NotNull(error.Errors);
        Assert.True(error.Errors!.ContainsKey(DocumentValidity.ValidToField));
    }

    /// <summary>
    /// The comparison is by DATE, not by instant — matching <c>NormalizePartyTerm</c>. A document
    /// valid for exactly one day is legitimate, and an end stamped earlier in the clock day than the
    /// start is the same calendar day, not an inversion.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -8)]
    public void Normalize_SameCalendarDay_IsAccepted(int dayOffset, int hourOffset)
    {
        var from = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var (_, validTo, _) = DocumentValidity.Normalize(from, from.AddDays(dayOffset).AddHours(hourOffset), null);

        Assert.NotNull(validTo);
    }

    /// <summary>
    /// AC 22 — each date is range-checked against the storage column independently, and the rejection
    /// names THAT date rather than always naming <c>ValidTo</c>: any of the three can be the offender.
    /// Without this the value passes every other rule and dies at <c>SaveChangesAsync</c> as an opaque
    /// <c>500</c>.
    /// </summary>
    [Fact]
    public void Normalize_ValidFromBelowTheStorableRange_ThrowsNamingValidFrom()
    {
        var belowRange = new DateTime(202, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var error = Assert.Throws<DomainValidationException>(
            () => DocumentValidity.Normalize(belowRange, null, null));

        Assert.Equal(400, error.StatusCode);
        Assert.True(error.Errors!.ContainsKey(DocumentValidity.ValidFromField));
    }

    [Fact]
    public void Normalize_IssuedAtBelowTheStorableRange_ThrowsNamingIssuedAt()
    {
        var error = Assert.Throws<DomainValidationException>(
            () => DocumentValidity.Normalize(null, null, DateTime.MinValue));

        Assert.True(error.Errors!.ContainsKey(DocumentValidity.IssuedAtField));
    }

    [Fact]
    public void Normalize_TheStorableBoundsThemselves_AreAccepted()
    {
        var (validFrom, validTo, _) = DocumentValidity.Normalize(
            DocumentValidity.MinStorable, DocumentValidity.MaxStorable, null);

        Assert.Equal(DocumentValidity.MinStorable, validFrom);
        Assert.Equal(DocumentValidity.MaxStorable, validTo);
    }

    /// <summary>
    /// The range check is a property of the storage column, not a judgement about plausible document
    /// dates: a warranty running to 2147 is a legitimate record and is not refused.
    /// </summary>
    [Fact]
    public void Normalize_FarFutureButStorable_IsAccepted()
    {
        var (_, validTo, _) = DocumentValidity.Normalize(
            null, new DateTime(2147, 1, 1, 0, 0, 0, DateTimeKind.Utc), null);

        Assert.NotNull(validTo);
    }

    /// <summary>
    /// The range check runs BEFORE the pairwise comparison, so an out-of-range value in an otherwise
    /// inverted pair is reported on its own field rather than being masked by the ordering message.
    /// </summary>
    [Fact]
    public void Normalize_OutOfRangeAndInverted_ReportsTheRangeFailureFirst()
    {
        var error = Assert.Throws<DomainValidationException>(() => DocumentValidity.Normalize(
            new DateTime(202, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            null));

        Assert.True(error.Errors!.ContainsKey(DocumentValidity.ValidFromField));
    }
}
