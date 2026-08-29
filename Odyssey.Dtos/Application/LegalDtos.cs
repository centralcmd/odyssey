using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Odyssey.Dtos.Application;

/// <summary>The two documents a user must accept (issue #354).</summary>
/// <remarks>
/// Serialized by name rather than by ordinal — the only enum on this API's wire surface that is. §7 pins
/// the contract as <c>"License" | "TermsOfService"</c>, and those same two strings are the values of the
/// pending-acceptance claim (<see cref="Odyssey.Dtos.Authorization.LegalClaims"/>), so a request
/// body, a claim value and a log line all read the same. An ordinal here would silently make
/// "documentType": 0 mean License in one place and nothing in the other.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<LegalDocumentType>))]
public enum LegalDocumentType
{
    License = 0,
    TermsOfService = 1,
}

/// <summary>The repository <c>LICENSE</c> text plus the digest acceptance is recorded against.</summary>
public sealed record LicenseDocument
{
    public required string Content { get; set; }

    /// <summary>Lowercase SHA-256 hex digest of <see cref="Content"/>.</summary>
    public required string Sha256 { get; set; }
}

/// <summary>The current published Terms of Service. The whole response is <c>null</c> when none exists yet.</summary>
public sealed record TermsOfServiceDocument
{
    public int Id { get; set; }

    public required string Content { get; set; }

    public DateTime PublishedAt { get; set; }
}

/// <summary>The calling user's own compliance state — never another user's.</summary>
public sealed record LegalComplianceStatus
{
    public bool LicenseCompliant { get; set; }

    public bool TosCompliant { get; set; }

    /// <summary><c>null</c> when no ToS version has ever been published.</summary>
    public int? CurrentTosVersionId { get; set; }
}

/// <summary>
/// One accept/decline response. <see cref="Accepted"/> is a nullable <c>bool</c> on purpose: with a
/// non-nullable one an omitted field would silently bind as <c>false</c> and record a decline the user
/// never gave, so <c>[Required]</c> on <c>bool?</c> is what turns "omitted" into a 400.
/// </summary>
public sealed record LegalDocumentResponse
{
    [Required]
    [EnumDataType(typeof(LegalDocumentType))]
    public LegalDocumentType? DocumentType { get; set; }

    [Required]
    public bool? Accepted { get; set; }

    /// <summary>
    /// Required for <see cref="LegalDocumentType.TermsOfService"/>, echoed from
    /// <c>GET /api/legal/terms-of-service/current</c> and verified server-side (409 on a stale value).
    /// </summary>
    public int? TosVersionId { get; set; }
}

/// <summary>Version-history row for the admin panel — metadata only, never the content.</summary>
public sealed record ExistingTermsOfServiceVersion
{
    public int Id { get; set; }

    public DateTime PublishedAt { get; set; }

    /// <summary><c>null</c> once the publishing admin's account has been deleted.</summary>
    public string? PublishedByUserId { get; set; }

    /// <summary><c>null</c> when the publisher is deleted or has no resolvable display name.</summary>
    public string? PublishedByDisplayName { get; set; }
}

/// <summary>One historical version including its full text, fetched on demand.</summary>
public sealed record TermsOfServiceVersionDetail
{
    public int Id { get; set; }

    public required string Content { get; set; }

    public DateTime PublishedAt { get; set; }

    public string? PublishedByUserId { get; set; }

    public string? PublishedByDisplayName { get; set; }
}

/// <summary>Publish request. Content is the only accepted field — the publisher and timestamp are server-set.</summary>
public sealed record NewTermsOfServiceVersion
{
    [Required]
    [StringLength(LegalLimits.MaxTermsOfServiceContentLength, MinimumLength = 1)]
    public required string Content { get; set; }
}

/// <summary>
/// The single definition of the ToS content cap — applied by the entity, the publish DTO and the admin
/// editor's character counter, so the three cannot drift apart.
/// </summary>
public static class LegalLimits
{
    public const int MaxTermsOfServiceContentLength = 50_000;
}
