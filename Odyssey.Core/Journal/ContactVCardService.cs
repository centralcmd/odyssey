using Odyssey.Core;
using Odyssey.Core.Finance;
using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Journal.Interop;

namespace Odyssey.Core.Journal;

/// <summary>
/// vCard (RFC 6350 v4.0) import/export for Contacts (issue #338), mirroring the shape of
/// <see cref="CalendarIcsService"/> (issue #330): export/import methods, <c>Max*</c> caps, an
/// <see cref="IsAcceptedContentType"/> helper. A minimal hand-rolled encoder/decoder is used rather than
/// a third-party vCard library — the property set this feature emits/consumes (§9) is small and fixed.
/// Both reads and writes flow exclusively through <see cref="ContactService"/>'s existing
/// get/create/update/child-CRUD methods (§10 item 3: no parallel read or write path, no new
/// mass-assignment surface).
/// </summary>
public class ContactVCardService
{
    // The repeatable-property bound was a `private const 200` here until issue #434 (key 12). It is
    // now admin-editable and TIGHTEN-ONLY, because the cost it guards is worse than the O(N^2) sibling
    // re-query the original comment described: ContactService.CreateAddress/CreateEmail/CreatePhone each
    // perform a sibling ToListAsync AND their own SaveChangesAsync per property, and that is multiplied
    // by ContactVCardMaxImportEntries, which ships "unlimited". Any numeric ceiling above 200 would be a
    // guess about a product of three unbounded terms, so the raise direction is removed instead.
    //
    // MaxVCardEntries only bounds the number of top-level VCARD blocks, never repeatable properties
    // (ADR/EMAIL/TEL) within a single one — which is why this bound exists separately at all.
    private const string UnnamedContact = "(unnamed)";

    private static readonly string[] AcceptedContentTypes =
        ["text/vcard", "text/x-vcard", "text/directory", "application/octet-stream", "text/plain"];

    private readonly OdysseyContext context;
    private readonly ContactService contactService;
    private readonly IImportExportLimitsLookup limits;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ContactVCardService> logger;

    public ContactVCardService(
        OdysseyContext context, ContactService contactService, IImportExportLimitsLookup limits,
        ILogger<ContactVCardService> logger, TimeProvider? timeProvider = null)
    {
        this.context = context;
        this.contactService = contactService;
        this.limits = limits;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ---------------------------------------------------------------- Export

    /// <summary>Exports a single contact as a single-entry .vcf. Null if it doesn't exist (the
    /// controller maps that to 404).</summary>
    public async Task<VCardExport?> ExportOneAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var contact = await contactService.Get(contactId, cancellationToken);
        if (contact is null)
        {
            return null;
        }

        var content = BuildDocument([contact]);
        return new VCardExport(BuildSingleFileName(contact.ResolvedDisplayName), content);
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
        ContactsQueryParams query, Stream output, Action<string, int> onReady, CancellationToken cancellationToken = default)
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
        await foreach (var chunk in contactService.StreamMatchingChunksAsync(query, ExportChunking.ChunkSize, cancellationToken))
        {
            var sb = new StringBuilder();
            foreach (var row in chunk)
            {
                AppendVCard(sb, row);
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            if (writtenBytes + bytes.Length > maxBytes)
            {
                logger.LogWarning(
                    "Contacts vCard export truncated at {WrittenBytes} bytes (cap {MaxBytes}); " +
                    "{WrittenRows}/{TotalRows} contacts delivered.",
                    writtenBytes, maxBytes, writtenRows, count);
                break;
            }

            await output.WriteAsync(bytes, cancellationToken);
            writtenBytes += bytes.Length;
            writtenRows += chunk.Count;
        }
    }

    private static bool HasFilters(ContactsQueryParams query) =>
        !string.IsNullOrWhiteSpace(query.Search) || query.Types is { Length: > 0 } || query.Status is not null;

    private static string BuildDocument(IEnumerable<ExistingContact> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            AppendVCard(sb, row);
        }

        return sb.ToString();
    }

    private static void AppendVCard(StringBuilder sb, ExistingContact row)
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
            AppendFolded(sb, $"N:{EscapeText(person.LastName)};{EscapeText(person.FirstName)};;;");
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

            if (person.RelationshipType is { } relationship)
            {
                AppendFolded(sb, $"X-ODYSSEY-RELATIONSHIP:{relationship}");
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
        }

        if (!string.IsNullOrWhiteSpace(row.Notes))
        {
            AppendFolded(sb, $"NOTE:{EscapeText(row.Notes)}");
        }

        AppendFolded(sb, $"REV:{row.UpdatedAt:yyyyMMddTHHmmssZ}");

        foreach (var address in row.Addresses)
        {
            var pref = address.IsPrimary ? ";PREF=1" : "";
            var street = address.Line2 is { Length: > 0 }
                ? $"{address.Line1} {address.Line2}"
                : address.Line1;
            AppendFolded(sb, $"ADR;TYPE={AddressTypeToken(address.Label)}{pref}:;;{EscapeText(street)};{EscapeText(address.City)};{EscapeText(address.Region ?? "")};{EscapeText(address.PostalCode ?? "")};{EscapeText(address.CountryCode)}");
        }

        foreach (var email in row.EmailAddresses)
        {
            var pref = email.IsPrimary ? ";PREF=1" : "";
            AppendFolded(sb, $"EMAIL;TYPE={EmailTypeToken(email.Label)}{pref}:{EscapeText(email.Value)}");
        }

        foreach (var phone in row.PhoneNumbers)
        {
            var pref = phone.IsPrimary ? ";PREF=1" : "";
            AppendFolded(sb, $"TEL;TYPE={PhoneTypeToken(phone.Label)}{pref}:{EscapeText(phone.Value)}");
        }

        AppendFolded(sb, "END:VCARD");
    }

    private static string AddressTypeToken(AddressLabel label) => label switch
    {
        AddressLabel.Home => "home",
        AddressLabel.Work => "work",
        AddressLabel.Billing => "billing",
        _ => "other",
    };

    private static string EmailTypeToken(EmailLabel label) => label switch
    {
        EmailLabel.Home => "home",
        EmailLabel.Work => "work",
        _ => "other",
    };

    private static string PhoneTypeToken(PhoneLabel label) => label switch
    {
        PhoneLabel.Home => "home",
        PhoneLabel.Work => "work",
        PhoneLabel.Mobile => "cell",
        _ => "other",
    };

    // Builds "<sanitized display name>.vcf" for a single-contact export, stripping quotes/control
    // characters/slashes for a safe Content-Disposition value (mirrors CalendarIcsService.BuildFileName).
    private static string BuildSingleFileName(string displayName)
    {
        var cleaned = new string(displayName.Where(c => !char.IsControl(c) && c is not ('"' or '\\' or '/')).ToArray()).Trim();
        return $"{(cleaned.Length == 0 ? "contact" : cleaned)}.vcf";
    }

    // ---------------------------------------------------------------- Import

    public async Task<VCardImportResult> ImportAsync(
        Stream file, long contentLength, string? contentType, CancellationToken cancellationToken = default)
    {
        if (!IsAcceptedContentType(contentType))
        {
            throw new DomainValidationException("The uploaded file must be a vCard file (text/vcard).");
        }

        var cap = await limits.GetAsync(cancellationToken);
        var maxImportBytes = cap.ContactVCardMaxImportBytes;
        var maxVCardEntries = cap.ContactVCardMaxImportEntries ?? int.MaxValue;

        if (contentLength > maxImportBytes)
        {
            throw new DomainValidationException($"The .vcf file exceeds the {maxImportBytes / (1024 * 1024)} MB limit.");
        }

        using var reader = ImportFileReader.OpenBoundedTextReader(file, maxImportBytes, ".vcf");

        var created = 0;
        var updated = 0;
        var skipped = new ImportSkipCollector(cap.ImportMaxSamplesPerSkipReason);
        var blockCount = 0;

        // SplitBlocksAsync yields each BEGIN:VCARD/END:VCARD block as it's found in the stream, so the
        // entry-count cap is enforced WHILE STREAMING — an over-cap file is rejected at the entry that
        // crosses the limit, not after every block has been read and materialized (issue #343 §5, sec
        // 3-3): the whole file is never held as one in-memory list of blocks.
        await foreach (var block in SplitBlocksAsync(reader, cancellationToken))
        {
            blockCount++;
            if (blockCount > maxVCardEntries)
            {
                throw new DomainValidationException($"The file contains more than {maxVCardEntries} vCard entries.");
            }

            var props = ParseProperties(block);
            var (sampleName, outcome, reason) = await ImportOneAsync(
                props, skipped, cap.ContactVCardMaxRepeatablePropertiesPerEntry, cancellationToken);
            switch (outcome)
            {
                case ImportOutcome.Created:
                    created++;
                    break;
                case ImportOutcome.Updated:
                    updated++;
                    break;
                case ImportOutcome.Skipped:
                    skipped.Add(reason!, sampleName);
                    break;
            }
        }

        if (blockCount == 0)
        {
            throw new DomainValidationException("The file could not be parsed as a valid vCard (.vcf) file.");
        }

        return new VCardImportResult
        {
            CreatedCount = created,
            UpdatedCount = updated,
            Skipped = skipped.ToGroups((reason, count, samples) => new VCardImportSkipGroup
            {
                Reason = reason,
                Count = count,
                SampleNames = samples,
            }),
        };
    }

    private enum ImportOutcome { Created, Updated, Skipped }

    private async Task<(string SampleName, ImportOutcome Outcome, string? Reason)> ImportOneAsync(
        Dictionary<string, List<VCardProperty>> props, ImportSkipCollector skipped,
        int maxRepeatableProperties, CancellationToken cancellationToken)
    {
        var uid = TextValue(props, "UID");
        var nRaw = RawValue(props, "N");
        var orgRaw = RawValue(props, "ORG");
        var kind = TextValue(props, "KIND");

        ContactType type;
        if (string.Equals(kind, "org", StringComparison.OrdinalIgnoreCase) || (orgRaw is not null && nRaw is null))
        {
            type = ContactType.Organization;
        }
        else if (nRaw is not null)
        {
            type = ContactType.Person;
        }
        else
        {
            return (UnnamedContact, ImportOutcome.Skipped, "Could not determine contact type (no N or ORG).");
        }

        var firstName = "";
        var lastName = "";
        var legalName = "";

        if (type == ContactType.Person)
        {
            var comps = nRaw is null ? [] : SplitUnescaped(nRaw);
            lastName = comps.Count > 0 ? UnescapeText(comps[0]).Trim() : "";
            firstName = comps.Count > 1 ? UnescapeText(comps[1]).Trim() : "";
        }
        else
        {
            var comps = orgRaw is null ? [] : SplitUnescaped(orgRaw);
            legalName = comps.Count > 0 ? UnescapeText(comps[0]).Trim() : "";
        }

        var personName = CollapseWhitespace($"{firstName} {lastName}");
        var sampleName = type == ContactType.Person
            ? (personName.Length > 0 ? personName : UnnamedContact)
            : (legalName.Length > 0 ? legalName : UnnamedContact);

        var fn = TextValue(props, "FN");
        var fallback = type == ContactType.Person
            ? CollapseWhitespace($"{firstName} {lastName}")
            : CollapseWhitespace(legalName);
        var displayNameOverride = fn is { Length: > 0 } && !string.Equals(fn, fallback, StringComparison.Ordinal) ? fn : null;

        var newContact = new NewContact
        {
            Type = type,
            DisplayName = displayNameOverride,
            Notes = TextValue(props, "NOTE"),
            Archived = false,
            ExternalUid = uid,
        };

        if (type == ContactType.Person)
        {
            var orgComps = orgRaw is null ? [] : SplitUnescaped(orgRaw);
            var company = orgComps.Count > 0 ? UnescapeText(orgComps[0]).Trim() : null;

            newContact.PersonDetails = new PersonDetailsDto
            {
                FirstName = firstName,
                LastName = lastName,
                DateOfBirth = ParseBirthday(TextValue(props, "BDAY")),
                Sex = ParseGender(TextValue(props, "GENDER")),
                RelationshipType = ParseRelationship(TextValue(props, "X-ODYSSEY-RELATIONSHIP")),
                Title = EmptyToNull(TextValue(props, "TITLE")),
                Company = EmptyToNull(company),
            };
        }
        else
        {
            newContact.OrganizationDetails = new OrganizationDetailsDto
            {
                LegalName = legalName,
                OrganizationNumber = EmptyToNull(TextValue(props, "X-ODYSSEY-ORG-NUMBER")),
                Website = ValidateHttpUrl(TextValue(props, "URL")),
            };
        }

        var existingId = uid is null ? null : await contactService.FindIdByExternalUid(uid, cancellationToken);

        try
        {
            // The contact write and its wholesale contact-collection replace must succeed or fail
            // together: ContactService's Create/Update/child-CRUD methods each commit with their
            // own SaveChangesAsync, so without an explicit transaction a mid-sequence failure (or crash)
            // could leave existing addresses/emails/phones deleted with nothing to replace them (issue
            // #338 review). CreateExecutionStrategy() is required rather than a bare BeginTransactionAsync
            // because OdysseyContext runs with EnableRetryOnFailure() in production — an ambient
            // transaction opened without it throws there (see DatabaseExtension.cs).
            var strategy = context.Database.CreateExecutionStrategy();
            var (contactId, isUpdate) = await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                Guid id;
                bool updated;
                if (existingId is { } existing)
                {
                    var existingRow = (await contactService.Get(existing, cancellationToken))!;
                    newContact.Archived = existingRow.Archived is not null; // vCard import never touches Archived (§9)
                    await contactService.Update(existing, newContact, cancellationToken);
                    id = existing;
                    updated = true;
                }
                else
                {
                    var createdRow = await contactService.Create(newContact, cancellationToken);
                    id = createdRow.ContactId;
                    updated = false;
                }

                await ReplaceContactCollections(
                    id, props, updated, sampleName, skipped, maxRepeatableProperties, cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                return (id, updated);
            });

            return (sampleName, isUpdate ? ImportOutcome.Updated : ImportOutcome.Created, null);
        }
        catch (DomainValidationException ex)
        {
            // A validation failure partway through Create/Update can leave a partially-mutated tracked
            // entity in the change tracker without ever reaching SaveChangesAsync. Since one long-lived
            // scoped OdysseyContext processes every entry in the import, that stale entity would
            // otherwise get flushed as a side effect of a LATER entry's SaveChangesAsync — clear it so a
            // skipped entry never leaks a partial write. The transaction above is never committed in
            // this path either, so no partial write reaches the database in the first place.
            context.ChangeTracker.Clear();
            return (sampleName, ImportOutcome.Skipped, ex.Message);
        }
    }

    // A UID-matched update replaces Addresses/EmailAddresses/PhoneNumbers wholesale with the vCard's
    // contents (§9) rather than merging; a fresh create simply has nothing to replace yet. An individual
    // repeatable block that fails validation is dropped rather than skipping the whole entry, but the
    // drop is still recorded in the shared ImportSkipCollector so it surfaces in VCardImportResult instead of
    // silently vanishing (issue #338 review) — the entry itself is still reported Created/Updated.
    private async Task ReplaceContactCollections(
        Guid contactId, Dictionary<string, List<VCardProperty>> props, bool isUpdate, string sampleName,
        ImportSkipCollector skipped, int maxRepeatableProperties, CancellationToken cancellationToken)
    {
        if (isUpdate)
        {
            foreach (var address in await contactService.GetAddresses(contactId, cancellationToken) ?? [])
            {
                await contactService.DeleteAddress(contactId, address.Id, cancellationToken);
            }

            foreach (var email in await contactService.GetEmails(contactId, cancellationToken) ?? [])
            {
                await contactService.DeleteEmail(contactId, email.Id, cancellationToken);
            }

            foreach (var phone in await contactService.GetPhones(contactId, cancellationToken) ?? [])
            {
                await contactService.DeletePhone(contactId, phone.Id, cancellationToken);
            }
        }

        foreach (var prop in CappedProperties(props, "ADR", "Address", sampleName, skipped, maxRepeatableProperties))
        {
            var address = ParseAddress(prop);
            if (address is null)
            {
                // ParseAddress pre-filters (missing street/city/country) rather than throwing, but the
                // drop is just as real as one caught below — report it the same way (issue #338 review).
                skipped.Add("Address dropped: missing a required field (street/city/country).", sampleName);
                continue;
            }

            try
            {
                await contactService.CreateAddress(contactId, address, cancellationToken);
            }
            catch (DomainValidationException ex)
            {
                skipped.Add($"Address dropped: {ex.Message}", sampleName);
            }
        }

        foreach (var prop in CappedProperties(props, "EMAIL", "Email address", sampleName, skipped, maxRepeatableProperties))
        {
            var email = ParseEmail(prop);
            if (email is null)
            {
                skipped.Add("Email address dropped: missing or not a valid email address.", sampleName);
                continue;
            }

            try
            {
                await contactService.CreateEmail(contactId, email, cancellationToken);
            }
            catch (DomainValidationException ex)
            {
                skipped.Add($"Email address dropped: {ex.Message}", sampleName);
            }
        }

        foreach (var prop in CappedProperties(props, "TEL", "Phone number", sampleName, skipped, maxRepeatableProperties))
        {
            var phone = ParsePhone(prop);
            if (phone is null)
            {
                skipped.Add("Phone number dropped: missing or not a valid phone number.", sampleName);
                continue;
            }

            try
            {
                await contactService.CreatePhone(contactId, phone, cancellationToken);
            }
            catch (DomainValidationException ex)
            {
                skipped.Add($"Phone number dropped: {ex.Message}", sampleName);
            }
        }
    }

    private static NewAddress? ParseAddress(VCardProperty prop)
    {
        var comps = SplitUnescaped(prop.RawValue);
        string Get(int i) => comps.Count > i ? UnescapeText(comps[i]).Trim() : "";

        var street = Get(2);
        var city = Get(3);
        var region = Get(4);
        var postal = Get(5);
        var country = Get(6);

        if (street.Length == 0 || city.Length == 0 || country.Length == 0)
        {
            return null;
        }

        return new NewAddress
        {
            Label = ExtractTypeToken(prop.Params, "home", "work", "billing") switch
            {
                "home" => AddressLabel.Home,
                "work" => AddressLabel.Work,
                "billing" => AddressLabel.Billing,
                _ => AddressLabel.Other,
            },
            IsPrimary = HasPref(prop.Params),
            Line1 = street,
            City = city,
            Region = EmptyToNull(region),
            PostalCode = EmptyToNull(postal),
            CountryCode = country,
        };
    }

    private static NewEmailAddress? ParseEmail(VCardProperty prop)
    {
        var value = UnescapeText(prop.RawValue).Trim();
        if (value.Length == 0 || !EmailValidator.IsValid(value))
        {
            return null;
        }

        return new NewEmailAddress
        {
            Label = ExtractTypeToken(prop.Params, "home", "work") switch
            {
                "home" => EmailLabel.Home,
                "work" => EmailLabel.Work,
                _ => EmailLabel.Other,
            },
            IsPrimary = HasPref(prop.Params),
            Value = value,
        };
    }

    private static NewPhoneNumber? ParsePhone(VCardProperty prop)
    {
        var raw = UnescapeText(prop.RawValue).Trim();
        var value = raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ? raw[4..] : raw;
        if (value.Length == 0 || !PhoneValidator.IsValid(value))
        {
            return null;
        }

        return new NewPhoneNumber
        {
            Label = ExtractTypeToken(prop.Params, "home", "work", "cell", "mobile") switch
            {
                "home" => PhoneLabel.Home,
                "work" => PhoneLabel.Work,
                "cell" or "mobile" => PhoneLabel.Mobile,
                _ => PhoneLabel.Other,
            },
            IsPrimary = HasPref(prop.Params),
            Value = value,
        };
    }

    private static readonly EmailAddressAttribute EmailValidator = new();
    private static readonly PhoneAttribute PhoneValidator = new();

    private static string? ExtractTypeToken(IReadOnlyDictionary<string, string> parameters, params string[] known)
    {
        if (!parameters.TryGetValue("TYPE", out var raw))
        {
            return null;
        }

        return raw.Trim('"').Split(',')
            .Select(t => t.Trim().ToLowerInvariant())
            .FirstOrDefault(t => known.Contains(t));
    }

    private static bool HasPref(IReadOnlyDictionary<string, string> parameters) => parameters.ContainsKey("PREF");

    private static DateTime? ParseBirthday(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return DateTime.TryParseExact(
            value, ["yyyyMMdd", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt
            : null;
    }

    private static Sex? ParseGender(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var first = SplitUnescaped(value).FirstOrDefault() ?? value;
        return UnescapeText(first).Trim().ToUpperInvariant() switch
        {
            "M" => Sex.Male,
            "F" => Sex.Female,
            _ => null,
        };
    }

    private static RelationshipType? ParseRelationship(string? value) =>
        value is not null && Enum.TryParse<RelationshipType>(value, ignoreCase: true, out var result) ? result : null;

    // Dropped (not skipped) if the value isn't a well-formed http/https URL (§9) — the contact-level
    // service-side Website check throws on a bad scheme, which would otherwise turn this into a
    // whole-entry skip instead of the "drop this one field" behavior the spec calls for.
    private static string? ValidateHttpUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme is "http" or "https" ? value : null;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();

    // ---------------------------------------------------------------- Parsing primitives

    private readonly record struct VCardProperty(IReadOnlyDictionary<string, string> Params, string RawValue);

    private static string? RawValue(Dictionary<string, List<VCardProperty>> props, string name) =>
        props.TryGetValue(name, out var list) && list.Count > 0 ? list[0].RawValue : null;

    // The unescaped, trimmed text of a property's first occurrence, or null if absent/blank.
    private static string? TextValue(Dictionary<string, List<VCardProperty>> props, string name)
    {
        var raw = RawValue(props, name);
        if (raw is null)
        {
            return null;
        }

        var value = UnescapeText(raw).Trim();
        return value.Length == 0 ? null : value;
    }

    private static IReadOnlyList<VCardProperty> Properties(Dictionary<string, List<VCardProperty>> props, string name) =>
        props.TryGetValue(name, out var list) ? list : [];

    // Bounds the repeatable properties of one type (ADR/EMAIL/TEL) actually processed for a single
    // entry — each one costs a sibling ToListAsync plus its own SaveChangesAsync, so processing all of
    // an unbounded count is quadratic in queries. The excess is reported once per entry rather than
    // once per dropped property, to keep the result payload from ballooning to match.
    //
    // The cap is a PARAMETER, not read inside this method: it is `static`, and the caller resolves one
    // settings snapshot for the whole import so a concurrent admin write cannot change the bound
    // between two entries of the same file (issue #434 key 12).
    private static IReadOnlyList<VCardProperty> CappedProperties(
        Dictionary<string, List<VCardProperty>> props, string name, string label, string sampleName,
        ImportSkipCollector skipped, int maxRepeatableProperties)
    {
        var all = Properties(props, name);
        if (all.Count <= maxRepeatableProperties)
        {
            return all;
        }

        skipped.Add(
            $"{label} dropped: more than {maxRepeatableProperties} in one entry — the rest were not imported.",
            sampleName);
        return all.Take(maxRepeatableProperties).ToList();
    }

    // Splits the file into logical BEGIN:VCARD/END:VCARD blocks (each a list of unfolded, raw property
    // lines), reading and unfolding lazily from the TextReader rather than materializing the whole
    // file as one string first (issue #343 §5): a block is yielded the moment its END:VCARD line is
    // recognized, so the caller's entry-count cap can reject a file at the entry that crosses it
    // instead of only after every block has been read. Lines outside any BEGIN/END pair are ignored
    // rather than treated as an error — mirrors the lenient "extension + successful parse are the real
    // gate" posture used for ICS import.
    private static async IAsyncEnumerable<List<string>> SplitBlocksAsync(
        TextReader reader, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<string>? current = null;
        // RFC 6350 §3.2 unfolding: a line starting with a space or tab is a continuation of the
        // previous line (with that one leading character stripped). One physical line of lookahead is
        // held in `pending` — a logical line isn't complete (and therefore isn't ready to apply) until
        // either the next physical line proves NOT to be a continuation of it, or EOF is reached.
        StringBuilder? pending = null;

        string? raw;
        while ((raw = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t') && pending is not null)
            {
                // Accumulate in the existing StringBuilder rather than `logicalLine += raw[1..]` —
                // repeated += allocates and copies the ENTIRE line built so far, making reconstruction
                // of a single line folded across N continuation lines O(N^2). A vCard producer can fold
                // a property value across arbitrarily many continuation lines (RFC 6350 §3.2 permits
                // this anywhere), so this was reachable with a well-formed file, not just a malicious
                // one (issue #338 review).
                pending.Append(raw, 1, raw.Length - 1);
                continue;
            }

            if (pending is not null && ApplyLine(pending.ToString(), ref current) is { } completed)
            {
                yield return completed;
            }

            pending = new StringBuilder(raw);
        }

        if (pending is not null && ApplyLine(pending.ToString(), ref current) is { } last)
        {
            yield return last;
        }
    }

    // Applies one complete (already-unfolded) logical line to the in-progress block, returning the
    // just-completed block when `line` was an END:VCARD that closes one, or null otherwise.
    private static List<string>? ApplyLine(string line, ref List<string>? current)
    {
        if (line.Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
        {
            current = [];
            return null;
        }

        if (line.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
        {
            var completed = current;
            current = null;
            return completed;
        }

        current?.Add(line);
        return null;
    }

    private static Dictionary<string, List<VCardProperty>> ParseProperties(List<string> lines)
    {
        var result = new Dictionary<string, List<VCardProperty>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var colonIndex = line.IndexOf(':');
            if (colonIndex < 0)
            {
                continue; // not a well-formed content line — ignore rather than fail the whole block
            }

            var left = line[..colonIndex];
            var rawValue = line[(colonIndex + 1)..];

            var segments = left.Split(';');
            var namePart = segments[0];
            var dotIndex = namePart.LastIndexOf('.');
            var name = (dotIndex >= 0 ? namePart[(dotIndex + 1)..] : namePart).Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < segments.Length; i++)
            {
                var eq = segments[i].IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }

                parameters[segments[i][..eq].Trim()] = segments[i][(eq + 1)..].Trim().Trim('"');
            }

            if (!result.TryGetValue(name, out var list))
            {
                list = [];
                result[name] = list;
            }

            list.Add(new VCardProperty(parameters, rawValue));
        }

        return result;
    }

    // Splits a raw structured-property value on unescaped ';' — an escaped "\;" is kept intact for the
    // subsequent per-component UnescapeText call rather than treated as a separator.
    private static List<string> SplitUnescaped(string value)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                sb.Append(value[i]).Append(value[i + 1]);
                i++;
                continue;
            }

            if (value[i] == ';')
            {
                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(value[i]);
        }

        parts.Add(sb.ToString());
        return parts;
    }

    // RFC 6350 §3.3 text escaping: backslash, comma, semicolon, newline.
    private static string EscapeText(string value)
    {
        var normalized = value.Replace("\r\n", "\n").Replace("\r", "\n");
        var sb = new StringBuilder(normalized.Length + 8);
        foreach (var ch in normalized)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case ',': sb.Append("\\,"); break;
                case ';': sb.Append("\\;"); break;
                case '\n': sb.Append("\\n"); break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString();
    }

    private static string UnescapeText(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                switch (value[i + 1])
                {
                    case 'n' or 'N': sb.Append('\n'); i++; continue;
                    case ',': sb.Append(','); i++; continue;
                    case ';': sb.Append(';'); i++; continue;
                    case '\\': sb.Append('\\'); i++; continue;
                }
            }

            sb.Append(value[i]);
        }

        return sb.ToString();
    }

    // RFC 6350 §3.2 line folding: a content line longer than 75 octets is wrapped by inserting CRLF +
    // a single leading space before each continuation segment. Folding never splits a multi-byte UTF-8
    // sequence.
    private static void AppendFolded(StringBuilder sb, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length <= 75)
        {
            sb.Append(line).Append("\r\n");
            return;
        }

        var pos = 0;
        var first = true;
        while (pos < bytes.Length)
        {
            var budget = first ? 75 : 74;
            var end = Math.Min(pos + budget, bytes.Length);
            while (end > pos + 1 && end < bytes.Length && (bytes[end] & 0xC0) == 0x80)
            {
                end--;
            }

            sb.Append(first ? "" : " ").Append(Encoding.UTF8.GetString(bytes, pos, end - pos)).Append("\r\n");
            pos = end;
            first = false;
        }
    }

    /// <summary>Whether the multipart part's content type is acceptable for a <c>.vcf</c> upload — the
    /// extension and the parse itself are the real validity gates (mirrors
    /// <see cref="CalendarIcsService.IsAcceptedContentType"/>).</summary>
    public static bool IsAcceptedContentType(string? contentType) =>
        ImportFileReader.IsAcceptedContentType(contentType, AcceptedContentTypes);
}

public sealed record VCardExport(string FileName, string Content);
