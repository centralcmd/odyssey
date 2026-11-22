using Odyssey.Dtos;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Journal.Interop;

namespace Odyssey.Core.Journal;

public partial class ContactVCardService
{
    // ---------------------------------------------------------------- Export

    /// <summary>Exports a single contact as a single-entry .vcf. Null if it doesn't exist (the
    /// controller maps that to 404).</summary>
    public async Task<VCardExport?> ExportOneAsync(
        Guid contactId, bool includeImages = false, CancellationToken cancellationToken = default)
    {
        var contact = await contactService.Get(contactId, cancellationToken);
        if (contact is null)
        {
            return null;
        }

        // includeImages defaults to FALSE here as well as on the bulk endpoint. Flipping the
        // single-contact default would silently change what an existing caller receives from a shipped
        // endpoint — the same objection §17 raises against re-authorizing one.
        var image = includeImages ? await LoadImageDataUriAsync(contact.AvatarFileId, cancellationToken) : null;
        var geocodes = await LoadGeocodesAsync([contact], cancellationToken);

        var sb = new StringBuilder();
        AppendVCard(sb, contact, image, geocodes);
        return new VCardExport(BuildSingleFileName(contact.ResolvedDisplayName), sb.ToString());
    }

    /// <summary>
    /// Reads one contact's image and encodes it as an RFC 6350 §6.2.4 <c>data:</c> URI, or returns
    /// <c>null</c> when there is none — or when the referenced file's stored content type is not
    /// avatar-legal, the same read-path check <c>GET .../avatar</c> applies, so a mis-pointed reference
    /// cannot export a tax statement into an address book.
    /// </summary>
    /// <remarks>
    /// Called <b>per contact</b>, never joined into a chunk projection. Adding a blob column to
    /// <c>StreamMatchingChunksAsync</c>'s projection would raise peak managed heap by
    /// <c>ChunkSize × image size</c> — reintroducing exactly the term the chunking exists to bound, and
    /// at the 2 MB cap that is hundreds of megabytes for a default chunk.
    /// </remarks>
    private async Task<string?> LoadImageDataUriAsync(Guid? avatarFileId, CancellationToken cancellationToken)
    {
        if (avatarFileId is not { } fileId)
        {
            return null;
        }

        // Through the one read path (issue #448); null unless avatar-legal.
        if (await avatarService.ReadImageAsync(fileId, cancellationToken) is not { } image)
        {
            return null;
        }

        return $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Bytes)}";
    }

    /// <summary>
    /// Streams every contact matching <paramref name="query"/> (omit all filters for "export all")
    /// directly to <paramref name="output"/> in row-keyset chunks (issue #343 §5 Goal 8), so peak
    /// managed heap is proportional to the chunk size rather than the matched row count. Throws
    /// <see cref="DomainValidationException"/> (→ 400) if the matched set exceeds the configured
    /// <c>ContactVCardMaxExportRows</c> cap — before any row is fetched, and before
    /// <paramref name="onReady"/> is invoked, so a caller that hasn't written any response headers yet
    /// can still turn this into a normal ProblemDetails response. <paramref name="onReady"/> is called
    /// exactly once, with the resolved file name and row count, after the pre-fetch count is known but
    /// before the first chunk is written — the caller's only chance to set response headers (including
    /// <c>X-Odyssey-Export-Rows</c>) before the body starts.
    /// <para>
    /// The configured <c>ContactVCardMaxExportMegabytes</c> cap (a follow-up to §5 Goal 8) is enforced
    /// differently: unlike the row-count cap above, the total output size isn't knowable until it's
    /// generated, so it can't be rejected up front the same way. Instead, once writing the next chunk
    /// would cross the cap, the stream simply stops (without writing that chunk) — the response then
    /// has fewer rows than <paramref name="onReady"/> already promised in the (already-sent)
    /// <c>X-Odyssey-Export-Rows</c> header, which the API client's existing completeness check
    /// (issue #343 §5 Goal 8 Tier 5) already treats as a failed download. No new client-side handling
    /// was needed for this.
    /// </para>
    /// </summary>
    public async Task ExportManyStreamingAsync(
        ContactsQueryParams query, Stream output, Action<string, int> onReady,
        bool includeImages = false, Action<int, int>? onTruncated = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveLimits = await limits.GetAsync(cancellationToken);
        var cap = effectiveLimits.ContactVCardMaxExportRows;
        var maxBytes = effectiveLimits.ContactVCardMaxExportBytes;
        var count = await contactService.CountMatchingAsync(query, cancellationToken);
        if (cap is { } max && count > max)
        {
            throw new DomainValidationException(
                $"{count} contacts match the current filters, which exceeds the maximum of {max} exportable at once. Narrow your filters and try again.");
        }

        var exportDate = timeProvider.GetUtcNow().UtcDateTime;
        var suffix = HasFilters(query) ? "filtered" : "all";
        var fileName = $"odyssey-contacts-{suffix}-{exportDate:yyyyMMdd}.vcf";
        onReady(fileName, count);

        var writtenBytes = 0L;
        var writtenRows = 0;
        var truncated = false;

        await foreach (var chunk in contactService.StreamMatchingChunksAsync(query, ExportChunking.ChunkSize, cancellationToken))
        {
            // One query per chunk, never per contact.
            var geocodes = await LoadGeocodesAsync(chunk, cancellationToken);

            if (includeImages)
            {
                // ONE IMAGE AT A TIME. Each contact's image is read, encoded and written as its card is
                // emitted, then released — so peak managed heap is bounded by a single image rather than
                // by ChunkSize images. Batching the chunk into one StringBuilder first, as the no-image
                // path below does, would hold every image in the chunk at once and reintroduce the
                // `ChunkSize × image size` term the chunking exists to bound (issue #86 §9).
                //
                // Truncation is row-granular here rather than chunk-granular for the same reason: a
                // partly-written chunk cannot be un-written once an image has already gone out.
                foreach (var row in chunk)
                {
                    var image = await LoadImageDataUriAsync(row.AvatarFileId, cancellationToken);

                    var rowBuilder = new StringBuilder();
                    AppendVCard(rowBuilder, row, image, geocodes);
                    var rowBytes = Encoding.UTF8.GetBytes(rowBuilder.ToString());

                    if (writtenBytes + rowBytes.Length > maxBytes)
                    {
                        truncated = true;
                        break;
                    }

                    await output.WriteAsync(rowBytes, cancellationToken);
                    writtenBytes += rowBytes.Length;
                    writtenRows++;
                }

                if (truncated)
                {
                    break;
                }

                continue;
            }

            var sb = new StringBuilder();
            foreach (var row in chunk)
            {
                AppendVCard(sb, row, imageDataUri: null, geocodes);
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            if (writtenBytes + bytes.Length > maxBytes)
            {
                truncated = true;
                break;
            }

            await output.WriteAsync(bytes, cancellationToken);
            writtenBytes += bytes.Length;
            writtenRows += chunk.Count;
        }

        if (!truncated)
        {
            return;
        }

        logger.LogWarning(
            "Contacts vCard export truncated at {WrittenBytes} bytes (cap {MaxBytes}); " +
            "{WrittenRows}/{TotalRows} contacts delivered.",
            writtenBytes, maxBytes, writtenRows, count);

        // The truncation is REPORTED, not hidden (issue #86 §9). With images on, the shipped 5 MB cap
        // makes truncation the common case rather than a corner, so a short document that looks
        // complete is the defect — the caller turns this into something the user sees before they treat
        // the file as a backup. Converting the truncation itself into an exception is out of scope
        // (§17): it is existing behaviour on an endpoint used today without images, and failing would
        // change what every current caller receives.
        onTruncated?.Invoke(writtenRows, count);
    }

    /// <summary>
    /// The coordinates of the contacts' geocoded addresses. Exported under <c>contacts.read</c> alone, like the
    /// street address they locate — a product decision: the card already carries the address, so its
    /// coordinates disclose nothing the address does not.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, AddressGeocode>> LoadGeocodesAsync(
        IReadOnlyCollection<ExistingContact> contacts, CancellationToken cancellationToken)
    {
        var addressIds = contacts.SelectMany(c => c.Addresses).Select(a => a.Id).ToList();
        return await contactService.GetAddressGeocodesAsync(addressIds, cancellationToken);
    }

    private static bool HasFilters(ContactsQueryParams query) =>
        !string.IsNullOrWhiteSpace(query.Search) || query.Types is { Length: > 0 } || query.Status is not null;

    /// <param name="imageDataUri">
    /// The contact's image as a <c>data:</c> URI, or <c>null</c> for no image — which is also what every
    /// caller passes when <c>includeImages</c> is off, so the emitted card is then byte-identical to
    /// what this produced before contact images existed.
    /// </param>
    /// <param name="geocodes">Coordinates by address id.</param>
    private static void AppendVCard(
        StringBuilder sb, ExistingContact row, string? imageDataUri, IReadOnlyDictionary<Guid, AddressGeocode> geocodes)
    {
        AppendFolded(sb, "BEGIN:VCARD");
        AppendFolded(sb, "VERSION:4.0");
        // Escaped like every other property value (defense-in-depth): ResolveExternalUid already
        // rejects control characters on write, but this keeps a pre-existing or otherwise-malformed
        // ExternalUid from ever corrupting the document's line structure on export (issue #338 review).
        AppendFolded(sb, $"UID:{EscapeText(row.ExternalUid)}");
        AppendFolded(sb, $"FN:{EscapeText(row.ResolvedDisplayName)}");
        AppendFolded(sb, row.Type == ContactType.Person ? "KIND:individual" : "KIND:org");

        if (row.Type == ContactType.Person && row.PersonDetails is { } person)
        {
            // Component 3 is the middle name (issue #48). The writer already emitted five components,
            // so a null MiddleName produces a BYTE-IDENTICAL line to the pre-change output.
            AppendFolded(sb, $"N:{EscapeText(person.LastName)};{EscapeText(person.FirstName)};{EscapeText(person.MiddleName ?? "")};;");
            if (!string.IsNullOrWhiteSpace(person.Title))
            {
                AppendFolded(sb, $"TITLE:{EscapeText(person.Title)}");
            }

            if (!string.IsNullOrWhiteSpace(person.Company))
            {
                AppendFolded(sb, $"ORG:{EscapeText(person.Company)}");
            }

            if (person.DateOfBirth is { } dob)
            {
                AppendFolded(sb, $"BDAY:{dob:yyyyMMdd}");
            }

            if (person.Sex is { } sex)
            {
                AppendFolded(sb, $"GENDER:{(sex == Sex.Male ? "M" : "F")}");
            }

            if (person.DateOfDeath is { } dod)
            {
                // A real registered property (RFC 6474), so it is used rather than inventing an
                // X-ODYSSEY- name for it.
                AppendFolded(sb, $"DEATHDATE:{dod:yyyyMMdd}");
            }
        }
        else if (row.Type == ContactType.Organization && row.OrganizationDetails is { } org)
        {
            AppendFolded(sb, $"ORG:{EscapeText(org.LegalName)}");

            if (!string.IsNullOrWhiteSpace(org.Website))
            {
                AppendFolded(sb, $"URL:{EscapeText(org.Website)}");
            }

            if (!string.IsNullOrWhiteSpace(org.OrganizationNumber))
            {
                AppendFolded(sb, $"X-ODYSSEY-ORG-NUMBER:{EscapeText(org.OrganizationNumber)}");
            }

            // No registered property exists for either, so both follow the established
            // X-ODYSSEY-ORG-NUMBER / -RELATIONSHIP naming (issue #48 §9).
            if (org.EstablishedDate is { } established)
            {
                AppendFolded(sb, $"X-ODYSSEY-ESTABLISHED:{established:yyyyMMdd}");
            }

            if (org.DissolvedDate is { } dissolved)
            {
                AppendFolded(sb, $"X-ODYSSEY-DISSOLVED:{dissolved:yyyyMMdd}");
            }
        }

        if (!string.IsNullOrWhiteSpace(row.Notes))
        {
            AppendFolded(sb, $"NOTE:{EscapeText(row.Notes)}");
        }

        AppendFolded(sb, $"REV:{row.UpdatedAt:yyyyMMddTHHmmssZ}");

        // No registered property says "archived", so it follows the X-ODYSSEY- naming. Read back, with its
        // date, only when an import CREATES the contact; an update keeps the target's own status (§9).
        if (row.Archived is { } archived)
        {
            AppendFolded(sb, $"{ArchivedProperty}:{archived:yyyyMMddTHHmmssZ}");
        }

        // The contact's one image (issue #86 §9), folded at 75 octets like every other property.
        // Person → PHOTO, Organization → LOGO: one storage slot, and the property name is purely
        // presentational — an import accepts either on either type.
        if (imageDataUri is not null)
        {
            AppendFolded(sb, $"{(row.Type == ContactType.Person ? "PHOTO" : "LOGO")}:{imageDataUri}");
        }

        // Aliases (issue #48 §9): one GROUPED pair per alias, in API order — the label is a grouped
        // PROPERTY, never a parameter.
        //
        // Two reasons a parameter cannot carry it. RFC 6350's quoted param-value is QSAFE-CHAR, which
        // EXCLUDES DQUOTE, so a label containing `"` has no in-band escape at all; and this parser is
        // not quote-aware (ParseProperties takes the first ':' and splits on every ';'), so `:`, `;`
        // or `"` breaks the parse and `a";TYPE=work;X-EVIL="b` INJECTS parameters that downstream
        // address books honour. This Label is the first user-controlled free text this codebase would
        // put in a vCard parameter — every existing one comes from a closed enum switch. A grouped
        // property removes the surface rather than defending it: its value is TEXT, which
        // EscapeText/UnescapeText already round-trip losslessly.
        //
        // A contact with no aliases emits nothing, so its card stays byte-identical to the previous
        // output.
        var aliasGroup = 0;
        foreach (var alias in row.Aliases)
        {
            aliasGroup++;
            AppendFolded(sb, $"item{aliasGroup}.NICKNAME:{EscapeText(alias.Value)}");
            if (!string.IsNullOrWhiteSpace(alias.Label))
            {
                AppendFolded(sb, $"item{aliasGroup}.{AliasLabelProperty}:{EscapeText(alias.Label)}");
            }
        }

        // Line 2 goes in the EXTENDED ADDRESS component (RFC 6350 §6.3.1: "e.g., apartment or suite number")
        // and is read back from there, rather than being appended to the street, which an import could
        // never split again — so Line 1 changed and the re-import matched a different place.
        //
        // A geocoded address is GROUPED with its GEO and TZ (adrN.ADR + adrN.GEO + adrN.TZ), the same shape
        // Apple uses for per-property labels. RFC 6350's ADR has GEO/TZ *parameters*, but a geo: URI value
        // contains ':' and this parser splits each line at its first ':' (see the alias comment above), so a
        // parameter would corrupt the line. An address without coordinates stays ungrouped, so its line is
        // byte-identical to the previous output.
        var addressGroup = 0;
        foreach (var address in row.Addresses)
        {
            var pref = address.IsPrimary ? ";PREF=1" : "";
            var geocode = geocodes.TryGetValue(address.Id, out var found) ? found : null;
            var group = geocode is null ? "" : $"{AddressGroupPrefix}{++addressGroup}.";
            AppendFolded(sb, $"{group}ADR;TYPE={AddressTypeToken(address.Label)}{pref}{ExtensionLabelParam(address.Label, AddressExtensionLabels)}:;{EscapeText(address.Line2 ?? "")};{EscapeText(address.Line1)};{EscapeText(address.City)};{EscapeText(address.Region ?? "")};{EscapeText(address.PostalCode ?? "")};{EscapeText(address.CountryCode)}");
            if (geocode is not null)
            {
                // Trailing zeros trimmed, so the line is the same whatever scale the provider materialises
                // (MariaDB's DECIMAL column pads; the InMemory provider does not).
                AppendFolded(sb, $"{group}GEO:geo:{FormatCoordinate(geocode.Latitude)},{FormatCoordinate(geocode.Longitude)}");
                if (!string.IsNullOrWhiteSpace(geocode.TimeZoneId))
                {
                    AppendFolded(sb, $"{group}TZ:{EscapeText(geocode.TimeZoneId)}");
                }
            }
        }

        foreach (var email in row.EmailAddresses)
        {
            var pref = email.IsPrimary ? ";PREF=1" : "";
            AppendFolded(sb, $"EMAIL;TYPE={EmailTypeToken(email.Label)}{pref}{ExtensionLabelParam(email.Label, EmailExtensionLabels)}:{EscapeText(email.Value)}");
        }

        foreach (var phone in row.PhoneNumbers)
        {
            var pref = phone.IsPrimary ? ";PREF=1" : "";
            AppendFolded(sb, $"TEL;TYPE={PhoneTypeToken(phone.Label)}{pref}{ExtensionLabelParam(phone.Label, PhoneExtensionLabels)}:{EscapeText(phone.Value)}");
        }

        AppendFolded(sb, "END:VCARD");
    }

    // The nearest standard vCard 4.0 token for each label. The organization vocabulary has no standard
    // counterpart, so those members map onto the closest one and carry X-ODYSSEY-LABEL for the finer
    // distinction (issue #47 §9). RFC 6350 dropped vCard 3.0's `postal` ADR type, which is why Postal
    // maps to `home` plus the extension.
    private static string AddressTypeToken(AddressLabel label) => label switch
    {
        AddressLabel.Home or AddressLabel.Postal => "home",
        AddressLabel.Work or AddressLabel.Visiting or AddressLabel.Registered or AddressLabel.Branch => "work",
        AddressLabel.Billing => "billing",
        _ => "other",
    };

    private static string EmailTypeToken(EmailLabel label) => label switch
    {
        EmailLabel.Home => "home",
        EmailLabel.Work or EmailLabel.General or EmailLabel.Support or EmailLabel.Sales
            or EmailLabel.Billing or EmailLabel.Claims => "work",
        _ => "other",
    };

    private static string PhoneTypeToken(PhoneLabel label) => label switch
    {
        PhoneLabel.Home => "home",
        PhoneLabel.Work => "work",
        PhoneLabel.Mobile => "cell",
        PhoneLabel.Switchboard or PhoneLabel.Support or PhoneLabel.Sales or PhoneLabel.Billing
            or PhoneLabel.Claims or PhoneLabel.Emergency or PhoneLabel.Direct => "voice,work",
        _ => "other",
    };

    private const string ExtensionLabelParamName = "X-ODYSSEY-LABEL";

    private static string FormatCoordinate(decimal value) =>
        value.ToString("0.##########", CultureInfo.InvariantCulture);

    /// <summary>The contact's archive timestamp; no registered vCard property carries it.</summary>
    private const string ArchivedProperty = "X-ODYSSEY-ARCHIVED";

    /// <summary>The earliest archive date an import keeps; anything earlier is clamped up to it.</summary>
    private static readonly DateTime EarliestArchiveDate = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Group prefix pairing an ADR with its GEO/TZ — distinct from the aliases' <c>item</c>.</summary>
    private const string AddressGroupPrefix = "adr";

    // The members with no standard token of their own — and ONLY those emit X-ODYSSEY-LABEL, so an
    // export containing no organization labels is byte-identical to the pre-#47 output.
    private static readonly AddressLabel[] AddressExtensionLabels =
        [AddressLabel.Postal, AddressLabel.Visiting, AddressLabel.Registered, AddressLabel.Branch];

    private static readonly EmailLabel[] EmailExtensionLabels =
        [EmailLabel.General, EmailLabel.Support, EmailLabel.Sales, EmailLabel.Billing, EmailLabel.Claims];

    private static readonly PhoneLabel[] PhoneExtensionLabels =
    [
        PhoneLabel.Switchboard, PhoneLabel.Support, PhoneLabel.Sales, PhoneLabel.Billing,
        PhoneLabel.Claims, PhoneLabel.Emergency, PhoneLabel.Direct,
    ];

    // The value is always an enum member name from a closed set, so nothing user-supplied reaches the
    // vCard parameter grammar — no injection surface on export.
    private static string ExtensionLabelParam<TLabel>(TLabel label, TLabel[] extensionLabels)
        where TLabel : struct, Enum =>
        Array.IndexOf(extensionLabels, label) >= 0 ? $";{ExtensionLabelParamName}={label}" : "";

    // Builds "<sanitized display name>.vcf" for a single-contact export, stripping quotes/control
    // characters/slashes for a safe Content-Disposition value (mirrors CalendarIcsService.BuildFileName).
    private static string BuildSingleFileName(string displayName)
    {
        var cleaned = new string(displayName.Where(c => !char.IsControl(c) && c is not ('"' or '\\' or '/')).ToArray()).Trim();
        return $"{(cleaned.Length == 0 ? "contact" : cleaned)}.vcf";
    }
}
