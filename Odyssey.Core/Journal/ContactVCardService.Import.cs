using Odyssey.Core.Imaging;
using Odyssey.Dtos;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Journal.Interop;
using Odyssey.Core.Locations;

namespace Odyssey.Core.Journal;

public partial class ContactVCardService
{
    // ---------------------------------------------------------------- Import

    public async Task<VCardImportResult> ImportAsync(
        Stream file, long contentLength, string? contentType, CancellationToken cancellationToken = default,
        LocationClaims? claims = null, string? userId = null) =>
        (await ImportWithOutcomeAsync(file, contentLength, contentType, cancellationToken, claims, userId)).Result;

    /// <summary>
    /// <see cref="ImportAsync"/>, plus the ids of the contacts whose type the import changed and of
    /// those whose type change was refused (issue #335 §7.5). The ids ride a Core-internal wrapper, never
    /// <see cref="VCardImportResult"/>: that is the wire type, and the lists exist only so the controller —
    /// which holds the principal — can write the actor-bearing audit lines.
    /// </summary>
    /// <param name="claims">
    /// The importer's location claims (issue #380 §8.4). <c>locations.create</c> is checked once for the whole
    /// file: without it every <c>ADR</c> is skipped and counted in
    /// <see cref="VCardImportResult.SkippedAddressCount"/>, and the contact is still imported — and an update keeps
    /// the contact's existing addresses rather than deleting them. Null is no capability at all; a trusted,
    /// non-HTTP caller passes <see cref="LocationClaims.Trusted"/> explicitly.
    /// </param>
    /// <param name="userId">The acting user, attributed on any place the import creates.</param>
    public async Task<VCardImportOutcome> ImportWithOutcomeAsync(
        Stream file, long contentLength, string? contentType, CancellationToken cancellationToken = default,
        LocationClaims? claims = null, string? userId = null)
    {
        var typeChanges = new TypeChangeIds();
        var addresses = new AddressImport(claims ?? LocationClaims.None, userId);
        if (!IsAcceptedContentType(contentType))
        {
            throw new DomainValidationException("The uploaded file must be a vCard file (text/vcard).");
        }

        var cap = await limits.GetAsync(cancellationToken);
        var maxImportBytes = cap.ContactVCardMaxImportBytes;
        var maxVCardEntries = cap.ContactVCardMaxImportEntries ?? int.MaxValue;

        // Resolved ONCE for the whole file, like the repeatable-property bound above, so a concurrent
        // admin write cannot change the avatar cap between two entries of the same import.
        var effectiveMaxAvatarBytes = await avatarService.GetEffectiveMaxBytesAsync(cancellationToken);

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
                props, skipped, cap.ContactVCardMaxRepeatablePropertiesPerEntry, effectiveMaxAvatarBytes,
                typeChanges, addresses, cancellationToken);
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

        var result = new VCardImportResult
        {
            CreatedCount = created,
            UpdatedCount = updated,
            Skipped = skipped.ToGroups((reason, count, samples) => new VCardImportSkipGroup
            {
                Reason = reason,
                Count = count,
                SampleNames = samples,
            }),
            AddressesLinked = addresses.Linked,
            // AddressesLinked − PlacesCreated is the number of addresses that matched an existing place,
            // which only a locations.read holder may learn (v8-L3).
            PlacesCreated = addresses.Claims.LocationsRead ? addresses.Created : null,
            SkippedAddressCount = addresses.Skipped,
        };

        return new VCardImportOutcome(result, typeChanges.Converted, typeChanges.Blocked);
    }

    /// <summary>
    /// The fixed skip reason for an entry whose type change a type-restricted link refused (issue #335
    /// §5.4). Never the exception's message: skips group by reason, so a per-entry count would split the
    /// groups, and it would disclose link counts to the importer.
    /// </summary>
    internal const string TypeChangeBlockedReason = "Contact type change blocked by existing links.";

    private enum ImportOutcome { Created, Updated, Skipped }

    /// <summary>The import's location claims and its address counts (issue #380 §8.4).</summary>
    private sealed class AddressImport(LocationClaims claims, string? userId)
    {
        public LocationClaims Claims { get; } = claims;

        public string? UserId { get; } = userId;

        public int Linked { get; set; }

        public int Created { get; set; }

        public int Skipped { get; set; }
    }

    private sealed class TypeChangeIds
    {
        public List<Guid> Converted { get; } = [];

        public List<Guid> Blocked { get; } = [];
    }

    private async Task<(string SampleName, ImportOutcome Outcome, string? Reason)> ImportOneAsync(
        Dictionary<string, List<VCardProperty>> props, ImportSkipCollector skipped,
        int maxRepeatableProperties, long effectiveMaxAvatarBytes, TypeChangeIds typeChanges,
        AddressImport addresses, CancellationToken cancellationToken)
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

        var middleName = "";

        if (type == ContactType.Person)
        {
            var comps = nRaw is null ? [] : SplitUnescaped(nRaw);
            lastName = comps.Count > 0 ? UnescapeText(comps[0]).Trim() : "";
            firstName = comps.Count > 1 ? UnescapeText(comps[1]).Trim() : "";
            // Component 3 (issue #48 §9). FN and N will legitimately DISAGREE on it: FN comes from
            // ResolvedDisplayName, which by design excludes the middle name, while N now carries it.
            // Accepted — FN is authoritative in vCard 4.0 and stays consistent with what Odyssey shows.
            middleName = comps.Count > 2 ? UnescapeText(comps[2]).Trim() : "";
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
            // Applies to a CREATE only: an update overwrites this with the target's own status below.
            Archived = TextValue(props, ArchivedProperty) is not null,
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
                MiddleName = EmptyToNull(middleName),
                DateOfBirth = ParseBirthday(TextValue(props, "BDAY")),
                // Same lenient posture BDAY already has: an unparseable date reads as absent rather
                // than skipping the whole entry (issue #48 §11).
                DateOfDeath = ParseVCardDate(TextValue(props, "DEATHDATE")),
                Sex = ParseGender(TextValue(props, "GENDER")),
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
                EstablishedDate = ParseVCardDate(TextValue(props, "X-ODYSSEY-ESTABLISHED")),
                DissolvedDate = ParseVCardDate(TextValue(props, "X-ODYSSEY-DISSOLVED")),
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
            var typeChanged = false;
            var (contactId, isUpdate) = await strategy.ExecuteAsync(async () =>
            {
                // This transaction is the owner ContactService.Update joins, so the retry is ours: start
                // each attempt from a clean tracker, or a retry would re-enter Update with the contact a
                // failed attempt already re-typed, see no type change, and skip the blocker check
                // (issue #335 §3.2). Earlier entries have committed, so nothing staged is lost.
                context.ChangeTracker.Clear();
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                Guid id;
                bool updated;
                if (existingId is { } existing)
                {
                    var existingRow = (await contactService.Get(existing, cancellationToken))!;
                    newContact.Archived = existingRow.Archived is not null; // vCard import never touches Archived (§9)
                    typeChanged = existingRow.Type != newContact.Type;
                    await contactService.Update(existing, newContact, cancellationToken);
                    id = existing;
                    updated = true;
                }
                else
                {
                    var createdRow = await contactService.Create(newContact, cancellationToken);
                    id = createdRow.ContactId;
                    updated = false;

                    if (newContact.Archived)
                    {
                        await RestoreArchiveDateAsync(id, TextValue(props, ArchivedProperty), cancellationToken);
                    }
                }

                await ReplaceContactCollections(
                    id, type, props, updated, sampleName, skipped, maxRepeatableProperties, addresses, cancellationToken);

                // The image rides the SAME transaction as the contact and its collections, so an entry
                // never commits half-imported. A failure here is recorded and the entry still imports:
                // one bad photo must not cost a user the contact.
                var (imageOutcome, imageReason) = await ApplyImportedImageAsync(
                    id, props, effectiveMaxAvatarBytes, maxRepeatableProperties, cancellationToken);
                if (imageOutcome == ImageOutcome.Skipped && imageReason is not null)
                {
                    skipped.Add(imageReason, sampleName);
                }

                await transaction.CommitAsync(cancellationToken);
                return (id, updated);
            });

            if (typeChanged)
            {
                typeChanges.Converted.Add(contactId);

                // Written at commit, so a later entry or a cancellation that aborts the import cannot
                // leave this committed change without a record. Ids only; the controller adds the actor.
                logger.LogInformation("vCard import changed the type of contact {ContactId}.", contactId);
            }

            return (sampleName, isUpdate ? ImportOutcome.Updated : ImportOutcome.Created, null);
        }
        catch (ContactTypeChangeBlockedException)
        {
            context.ChangeTracker.Clear();
            typeChanges.Blocked.Add(existingId!.Value);
            return (sampleName, ImportOutcome.Skipped, TypeChangeBlockedReason);
        }
        // DomainException, not DomainValidationException: the contact-method label scope check rejects
        // with DomainUnprocessableException (issue #47 §9), which would otherwise escape this catch and
        // abort the whole import with a 500 and a dirty change tracker.
        catch (DomainException ex)
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

    /// <summary>
    /// Replaces the archive timestamp <see cref="ContactService.Create"/> stamped (now) with the one the card
    /// carries, inside the entry's transaction. A value that does not parse keeps the contact archived as of
    /// now; a future one is clamped to now, since a contact cannot have been archived later than it was
    /// imported, and one before <see cref="EarliestArchiveDate"/> is clamped up to it, so a year-1 card can
    /// never reach the database as a DATETIME outside its range and abort the whole import.
    /// </summary>
    private async Task RestoreArchiveDateAsync(Guid contactId, string? value, CancellationToken cancellationToken)
    {
        if (!DateTime.TryParseExact(
                value, ["yyyyMMdd'T'HHmmss'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyyMMdd", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var archivedAt))
        {
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var clamped = archivedAt > now ? now : archivedAt < EarliestArchiveDate ? EarliestArchiveDate : archivedAt;
        var contact = await context.Contacts.FirstAsync(c => c.ContactId == contactId, cancellationToken);
        contact.Archived = DateTime.SpecifyKind(clamped, DateTimeKind.Utc);
        await context.SaveChangesAsync(cancellationToken);
    }

    // ── Imported contact image (issue #86 §9) ─────────────────────────────────────────────────────

    /// <summary>Property names carrying a contact image. Both are accepted on both contact types.</summary>
    private static readonly string[] ImageProperties = ["PHOTO", "LOGO"];

    /// <summary>What an entry's image amounted to.</summary>
    private enum ImageOutcome
    {
        /// <summary>No PHOTO/LOGO on the entry. An existing avatar is left UNCHANGED — never cleared.</summary>
        Absent,

        /// <summary>A valid image was decoded and attached.</summary>
        Attached,

        /// <summary>The entry had an image the importer would not take. The entry itself still imports.</summary>
        Skipped,
    }

    /// <summary>
    /// Resolves, validates and attaches an entry's image inside the entry's own transaction.
    ///
    /// <para>
    /// <b>An entry carrying no image leaves an existing avatar unchanged.</b> Import is an upsert on
    /// <c>ExternalUid</c>, and most address books do not round-trip images — so treating "no PHOTO" as
    /// "clear the picture" would silently strip every avatar on the first re-import.
    /// </para>
    ///
    /// <para>
    /// A failed image never fails the entry: one bad photo must not cost a user the contact. The
    /// failure becomes a skip-reason sample in the existing import summary instead.
    /// </para>
    /// </summary>
    private async Task<(ImageOutcome Outcome, string? Reason)> ApplyImportedImageAsync(
        Guid contactId, Dictionary<string, List<VCardProperty>> props, long effectiveMaxBytes,
        int maxRepeatableProperties, CancellationToken cancellationToken)
    {
        var candidates = ImageProperties.SelectMany(name => Properties(props, name)).ToList();
        if (candidates.Count == 0)
        {
            return (ImageOutcome.Absent, null);
        }

        // At most ONE image per entry; the first valid one wins and any further occurrence counts
        // against the repeatable-property bound like any other repeated property.
        var considered = candidates.Take(Math.Max(1, maxRepeatableProperties)).ToList();

        string? lastReason = null;
        foreach (var candidate in considered)
        {
            var (bytes, contentType, reason) = DecodeImageProperty(candidate, effectiveMaxBytes);
            if (bytes is null)
            {
                lastReason ??= reason;
                continue;
            }

            ValidatedImage validated;
            try
            {
                // The IDENTICAL pipeline an upload runs: allow-list, magic bytes, byte cap, dimension
                // cap, animation check, metadata strip, output re-validation.
                validated = avatarService.ValidateForImport(bytes, contentType, effectiveMaxBytes);
            }
            catch (DomainValidationException ex)
            {
                lastReason ??= ex.Message;
                continue;
            }

            var contact = await context.Contacts.FirstOrDefaultAsync(c => c.ContactId == contactId, cancellationToken);
            if (contact is null)
            {
                return (ImageOutcome.Skipped, "The contact could not be loaded to attach its image.");
            }

            // A valid image REPLACES an existing one — and that replace applies the shared release rule
            // like any other, so the outgoing file is deleted unless it is not avatar-legal.
            try
            {
                // The write first, then the release of the outgoing image, staged together (issue #448).
                await avatarService.StageReplaceAsync(contact, validated, userId: null, "vcard-import", cancellationToken);
            }
            catch (Exception ex) when (ex is FileStorage.FileStorageUnavailableException or FileStorage.FileStorageFullException)
            {
                // A storage fault never fails the import (issue #448 §3.3): the contact keeps no new image and the image
                // is reported skipped. A failed write stages nothing (the row is added only after the bytes are stored), so nothing is left for a later save.
                return (ImageOutcome.Skipped, "File storage isn't available, so the image wasn't saved.");
            }

            await context.SaveChangesAsync(cancellationToken);

            return (ImageOutcome.Attached, null);
        }

        return (ImageOutcome.Skipped, lastReason ?? "The entry's image could not be read.");
    }

    /// <summary>
    /// Decodes one <c>PHOTO</c>/<c>LOGO</c> property value. Handles both shapes in the wild: a vCard 4.0
    /// <c>data:</c> URI, and a vCard 3.0 <c>ENCODING=b</c> base64 property value — the latter being what
    /// most address books actually export.
    ///
    /// <para>
    /// <b>A URI reference is ignored and never dereferenced.</b> Fetching one would be a server-side
    /// request forgery primitive driven by an uploaded file: the attacker picks the URL, the server
    /// makes the request from inside the deployment, and the response lands in a contact record.
    /// </para>
    ///
    /// <para>
    /// <b>Bounds are checked before allocation.</b> The encoded length determines the decoded length
    /// arithmetically (3 bytes per 4 characters), so an over-cap image is rejected <i>without</i> being
    /// base64-decoded into memory. That matters because <c>ContactVCardMaxImportEntries</c> ships
    /// unlimited against an import byte cap sized for text.
    /// </para>
    /// </summary>
    private static (byte[]? Bytes, string? ContentType, string? Reason) DecodeImageProperty(
        VCardProperty property, long effectiveMaxBytes)
    {
        var raw = property.RawValue.Trim();
        if (raw.Length == 0)
        {
            return (null, null, "The entry's image was empty.");
        }

        string base64;
        string? contentType;

        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = raw.IndexOf(',');
            var meta = comma < 0 ? "" : raw[5..comma];
            if (comma < 0 || !meta.Contains("base64", StringComparison.OrdinalIgnoreCase))
            {
                return (null, null, "The entry's image is not a base64 data URI.");
            }

            contentType = meta.Split(';')[0].Trim();
            base64 = raw[(comma + 1)..];
        }
        else if (IsBase64Encoded(property))
        {
            contentType = ContentTypeFromTypeParameter(property);
            base64 = raw;
        }
        else
        {
            // Anything else is a URI reference — http(s) or otherwise. Skipped, never fetched.
            return (null, null, "The entry's image is a link rather than embedded data, so it was not imported.");
        }

        base64 = StripWhitespace(base64);
        if (base64.Length == 0)
        {
            return (null, null, "The entry's image was empty.");
        }

        // Arithmetic bound, BEFORE the decode allocates anything.
        var maximumDecoded = base64.Length / 4L * 3L;
        if (maximumDecoded > effectiveMaxBytes)
        {
            return (null, null,
                $"The entry's image exceeds the {Math.Round(effectiveMaxBytes / (1024d * 1024d), 1)} MB limit, so it was not imported.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return (null, null, "The entry's image could not be decoded.");
        }

        return (bytes, contentType, null);
    }

    /// <summary>vCard 3.0's <c>ENCODING=b</c> (and the older <c>ENCODING=BASE64</c>).</summary>
    private static bool IsBase64Encoded(VCardProperty property) =>
        property.Params.TryGetValue("ENCODING", out var encoding)
        && (encoding.Equals("b", StringComparison.OrdinalIgnoreCase)
            || encoding.Equals("base64", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// vCard 3.0 names the format in a <c>TYPE</c> parameter (<c>TYPE=JPEG</c>) rather than as a media
    /// type. It is only a hint — the magic-byte check in the shared pipeline is what actually decides,
    /// and a wrong hint is rejected there rather than trusted here.
    /// </summary>
    private static string? ContentTypeFromTypeParameter(VCardProperty property)
    {
        if (!property.Params.TryGetValue("TYPE", out var type))
        {
            return null;
        }

        return type.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" or "image/jpeg" or "image/jpg" => "image/jpeg",
            "png" or "image/png" => "image/png",
            "webp" or "image/webp" => "image/webp",
            _ => null,
        };
    }

    /// <summary>Unfolded base64 can still carry the whitespace some producers pad it with.</summary>
    private static string StripWhitespace(string value) =>
        value.Any(char.IsWhiteSpace) ? new string([.. value.Where(c => !char.IsWhiteSpace(c))]) : value;

    // A UID-matched update replaces Addresses/EmailAddresses/PhoneNumbers wholesale with the vCard's
    // contents (§9) rather than merging; a fresh create simply has nothing to replace yet. An individual
    // repeatable block that fails validation is dropped rather than skipping the whole entry, but the
    // drop is still recorded in the shared ImportSkipCollector so it surfaces in VCardImportResult instead of
    // silently vanishing (issue #338 review) — the entry itself is still reported Created/Updated.
    private async Task ReplaceContactCollections(
        Guid contactId, ContactType contactType, Dictionary<string, List<VCardProperty>> props, bool isUpdate,
        string sampleName, ImportSkipCollector skipped, int maxRepeatableProperties, AddressImport addresses,
        CancellationToken cancellationToken)
    {
        // Aliases MERGE rather than replace, unlike the three collections below (issue #48 §9): an
        // import must never delete an alias a person typed, and must never overwrite a label they
        // typed with a blank one.
        await MergeAliasesAsync(contactId, props, sampleName, skipped, cancellationToken);

        if (isUpdate)
        {
            // The replace is only a replace when the importer may re-create what it removes: without
            // locations.create every ADR below is skipped, so deleting first would leave the contact with no
            // addresses at all. Their existing ones stay instead (issue #380 security review, observation 1).
            var addressRows = addresses.Claims.LocationsCreate
                ? await contactService.GetAddresses(contactId, cancellationToken) ?? []
                : [];
            foreach (var address in addressRows)
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
            // A link-level skip, counted and never listed: the contact is still imported, and a skip group
            // would echo its name (v8-L4).
            if (!addresses.Claims.LocationsCreate)
            {
                addresses.Skipped++;
                continue;
            }

            var (address, geocodeDropped) = ParseAddress(prop, contactType, props);
            if (address is null)
            {
                // ParseAddress pre-filters (missing street/city/country) rather than throwing, but the
                // drop is just as real as one caught below — report it the same way (issue #338 review).
                skipped.Add("Address dropped: missing a required field (street/city/country).", sampleName);
                continue;
            }

            if (geocodeDropped)
            {
                // The address still imports; only its coordinates are left out.
                skipped.Add(
                    "Address coordinates dropped: GEO needs a valid latitude, longitude and IANA time zone (TZ).",
                    sampleName);
            }

            try
            {
                var (linked, placeCreated) = await contactService.CreateAddressCoreAsync(
                    contactId, address, addresses.Claims, addresses.UserId, cancellationToken);
                if (linked is not null)
                {
                    addresses.Linked++;
                    if (placeCreated)
                    {
                        addresses.Created++;
                    }
                }
            }
            catch (DomainException ex)
            {
                skipped.Add($"Address dropped: {ex.Message}", sampleName);
            }
        }

        foreach (var prop in CappedProperties(props, "EMAIL", "Email address", sampleName, skipped, maxRepeatableProperties))
        {
            var email = ParseEmail(prop, contactType);
            if (email is null)
            {
                skipped.Add("Email address dropped: missing or not a valid email address.", sampleName);
                continue;
            }

            try
            {
                await contactService.CreateEmail(contactId, email, cancellationToken);
            }
            catch (DomainException ex)
            {
                skipped.Add($"Email address dropped: {ex.Message}", sampleName);
            }
        }

        foreach (var prop in CappedProperties(props, "TEL", "Phone number", sampleName, skipped, maxRepeatableProperties))
        {
            var phone = ParsePhone(prop, contactType);
            if (phone is null)
            {
                skipped.Add("Phone number dropped: missing or not a valid phone number.", sampleName);
                continue;
            }

            try
            {
                await contactService.CreatePhone(contactId, phone, cancellationToken);
            }
            catch (DomainException ex)
            {
                skipped.Add($"Phone number dropped: {ex.Message}", sampleName);
            }
        }
    }

    /// <summary>The grouped label property carrying an alias's free-text label (issue #48 §9).</summary>
    private const string AliasLabelProperty = "X-ODYSSEY-ALIAS-LABEL";

    /// <summary>
    /// Adds the card's aliases to the contact, never removing one.
    ///
    /// <para>
    /// An incoming value that already exists is left <b>alone, its stored label included</b>. The cap
    /// counts the contact's total, and the excess is a counted reason group rather than a skip — the
    /// contact still imports (§11).
    /// </para>
    /// </summary>
    private async Task MergeAliasesAsync(
        Guid contactId, Dictionary<string, List<VCardProperty>> props, string sampleName,
        ImportSkipCollector skipped, CancellationToken cancellationToken)
    {
        var candidates = ParseAliases(props);
        if (candidates.Count == 0)
        {
            return;
        }

        var existing = await contactService.GetAliases(contactId, cancellationToken) ?? [];
        var kept = existing.Select(a => a.Value).ToList();
        var dropped = 0;

        foreach (var candidate in candidates)
        {
            if (kept.Any(value => AliasValueEquals(value, candidate.Value)))
            {
                continue; // already present — its stored label wins
            }

            if (kept.Count >= ContactAliasRules.MaxPerContact)
            {
                dropped++;
                continue;
            }

            try
            {
                await contactService.CreateAlias(contactId, candidate, cancellationToken);
                kept.Add(candidate.Value);
            }
            catch (DomainException ex) when (ex is DomainValidationException or DomainConflictException or DomainUnprocessableException)
            {
                // Same posture as the three collections below: one bad repeatable property is dropped
                // and reported, not escalated into a whole-entry skip.
                skipped.Add($"Alias dropped: {ex.Message}", sampleName);
            }
        }

        if (dropped > 0)
        {
            skipped.Add(
                $"Aliases dropped: more than {ContactAliasRules.MaxPerContact} in one entry — the rest were not imported.",
                sampleName);
        }
    }

    /// <summary>
    /// The card's aliases, in document order, de-duplicated case- and accent-insensitively with the
    /// first occurrence (and its label) winning.
    ///
    /// <para>
    /// Pairing is on the <b>group</b>: a <c>NICKNAME</c> takes the label from an
    /// <c>X-ODYSSEY-ALIAS-LABEL</c> sharing its group. The comma split is conditioned on
    /// <i>"our label is present"</i>, NOT on <i>"the property is grouped"</i> — grouping is a general
    /// vCard mechanism and an Apple-style <c>item1.NICKNAME:Kari,KH</c> with no label beside it must
    /// still split into two aliases.
    /// </para>
    ///
    /// <para>
    /// Four edge cases, so a mis-attached label cannot mislabel a maiden name: an ungrouped label is
    /// ignored; a group with two labels takes the first; a group with two <c>NICKNAME</c>s labels the
    /// first only; a label-only group creates no alias.
    /// </para>
    /// </summary>
    private static List<NewContactAlias> ParseAliases(Dictionary<string, List<VCardProperty>> props)
    {
        var labelsByGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in Properties(props, AliasLabelProperty))
        {
            // An ungrouped label belongs to no alias — ignored rather than applied to the first one.
            if (prop.Group is not { Length: > 0 } group || labelsByGroup.ContainsKey(group))
            {
                continue; // first label in a group wins
            }

            labelsByGroup[group] = UnescapeText(prop.RawValue);
        }

        var result = new List<NewContactAlias>();
        var labelledGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in Properties(props, "NICKNAME"))
        {
            string? label = null;
            if (prop.Group is { Length: > 0 } group
                && labelsByGroup.TryGetValue(group, out var groupLabel)
                && labelledGroups.Add(group))
            {
                label = groupLabel;
            }

            // A labelled NICKNAME is one alias: its label describes that whole value, so splitting it
            // would attach one label to several unrelated names.
            var values = label is null
                ? SplitUnescapedOn(prop.RawValue, ',').Select(UnescapeText)
                : [UnescapeText(prop.RawValue)];

            foreach (var raw in values)
            {
                var value = CollapseWhitespace(StripControlCharacters(raw));
                if (value.Length == 0)
                {
                    continue;
                }

                if (value.Length > ContactAliasRules.MaxValueLength)
                {
                    value = value[..ContactAliasRules.MaxValueLength];
                }

                if (result.Any(a => AliasValueEquals(a.Value, value)))
                {
                    continue;
                }

                var cleanedLabel = CollapseWhitespace(StripControlCharacters(label ?? ""));
                // Truncated rather than rejected — the informative part survives, matching the
                // "drop this one field" posture the rest of the import takes (§9).
                if (cleanedLabel.Length > ContactAliasRules.MaxLabelLength)
                {
                    cleanedLabel = cleanedLabel[..ContactAliasRules.MaxLabelLength];
                }

                result.Add(new NewContactAlias { Value = value, Label = EmptyToNull(cleanedLabel) });
            }
        }

        return result;
    }

    private static bool AliasValueEquals(string left, string right) =>
        string.Compare(left, right, CultureInfo.InvariantCulture,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;

    /// <summary>
    /// Strips C0 control characters and DEL rather than rejecting them (issue #48 §9).
    ///
    /// <para>
    /// <see cref="UnescapeText"/> turns an escaped newline into a real <c>U+000A</c> before the value
    /// reaches <c>ContactService</c>, so a <c>NICKNAME</c> carrying one would otherwise throw and cost the
    /// user the whole card — or, worse, persist a control character that feeds straight back into the
    /// next export. The HTTP path still <b>rejects</b> the same characters; stripping here is the
    /// import's truncate-don't-reject posture, not a relaxation of the rule.
    /// </para>
    /// </summary>
    private static string StripControlCharacters(string value) =>
        value.Any(char.IsControl) ? new string([.. value.Where(ch => !char.IsControl(ch))]) : value;

    /// <summary>
    /// One <c>ADR</c>, plus whether a GEO grouped with it had to be left out. Line 2 is the extended-address
    /// component; when that is empty, a multi-line street (Apple's shape) splits at its first newline. The
    /// country is resolved from a name or code (<see cref="VCardCountry"/>).
    /// </summary>
    private static (NewAddress? Address, bool GeocodeDropped) ParseAddress(
        VCardProperty prop, ContactType contactType, Dictionary<string, List<VCardProperty>> props)
    {
        var comps = SplitUnescaped(prop.RawValue);
        string Get(int i) => comps.Count > i ? UnescapeText(comps[i]).Trim() : "";

        var extended = Get(1);
        var street = Get(2);
        var city = Get(3);
        var region = Get(4);
        var postal = Get(5);
        var country = VCardCountry.Resolve(Get(6));

        var streetLines = street.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line1 = streetLines.Length > 0 ? streetLines[0] : "";
        // Extra street lines are never dropped: with an extended address too, they come first, then it.
        var line2 = string.Join(", ", streetLines.Skip(1)
            .Append(CollapseWhitespace(extended.Replace('\n', ' ')))
            .Where(part => part.Length > 0));

        if (line1.Length == 0 || city.Length == 0 || country is null)
        {
            return (null, false);
        }

        var (geocode, geocodeDropped) = ParseGroupedGeocode(prop.Group, props);

        return (new NewAddress
        {
            Label = ContactLabelScope.Clamp(
                ExtensionLabel<AddressLabel>(prop.Params) ?? ExtractTypeToken(prop.Params, "home", "work", "billing") switch
                {
                    "home" => AddressLabel.Home,
                    "work" => AddressLabel.Work,
                    "billing" => AddressLabel.Billing,
                    _ => AddressLabel.Other,
                },
                contactType),
            IsPrimary = HasPref(prop.Params),
            Line1 = line1,
            Line2 = EmptyToNull(line2),
            City = city,
            Region = EmptyToNull(region),
            PostalCode = EmptyToNull(postal),
            CountryCode = country,
            // Applied only if this address CREATES a place; a match on the address key links the existing
            // place unchanged, so an import never overwrites coordinates someone curated (issue #380 §5.3a).
            Latitude = geocode?.Latitude,
            Longitude = geocode?.Longitude,
            TimeZoneId = geocode?.TimeZoneId,
            Source = geocode is null ? null : LocationSource.Manual,
        }, geocodeDropped);
    }

    /// <summary>
    /// The GEO and TZ sharing an <c>ADR</c>'s group, validated by the same rules a request's geocode meets
    /// (<see cref="LocationGeocode.From"/>). An ungrouped GEO describes the contact rather than an address
    /// and is ignored. A GEO that cannot be used — no TZ, a malformed value, out of range — is dropped and
    /// reported, never allowed to cost the address.
    /// </summary>
    private static (LocationGeocode? Geocode, bool Dropped) ParseGroupedGeocode(
        string? group, Dictionary<string, List<VCardProperty>> props)
    {
        if (group is null)
        {
            return (null, false);
        }

        var geo = Properties(props, "GEO").FirstOrDefault(p => string.Equals(p.Group, group, StringComparison.OrdinalIgnoreCase));
        if (geo.RawValue is null)
        {
            return (null, false);
        }

        var tz = Properties(props, "TZ").FirstOrDefault(p => string.Equals(p.Group, group, StringComparison.OrdinalIgnoreCase));
        var zone = tz.RawValue is null ? null : UnescapeText(tz.RawValue).Trim();

        if (!TryParseGeo(geo.RawValue, out var latitude, out var longitude))
        {
            return (null, true);
        }

        try
        {
            return (LocationGeocode.From(latitude, longitude, zone, LocationSource.Manual, sourceReference: null), false);
        }
        catch (DomainException)
        {
            return (null, true);
        }
    }

    /// <summary>A vCard 4.0 <c>geo:lat,lon[;…]</c> URI, or vCard 3.0's <c>lat;lon</c>.</summary>
    private static bool TryParseGeo(string raw, out decimal latitude, out decimal longitude)
    {
        latitude = 0;
        longitude = 0;
        var value = raw.Trim();
        string[] parts;
        if (value.StartsWith("geo:", StringComparison.OrdinalIgnoreCase))
        {
            var coordinates = value[4..];
            var semicolon = coordinates.IndexOf(';');
            parts = (semicolon >= 0 ? coordinates[..semicolon] : coordinates).Split(',');
        }
        else
        {
            parts = value.Split(';');
        }

        return parts.Length >= 2
            && decimal.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out latitude)
            && decimal.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out longitude);
    }

    private static NewEmailAddress? ParseEmail(VCardProperty prop, ContactType contactType)
    {
        var value = UnescapeText(prop.RawValue).Trim();
        if (value.Length == 0 || !EmailValidator.IsValid(value))
        {
            return null;
        }

        return new NewEmailAddress
        {
            Label = ContactLabelScope.Clamp(
                ExtensionLabel<EmailLabel>(prop.Params) ?? ExtractTypeToken(prop.Params, "home", "work") switch
                {
                    "home" => EmailLabel.Home,
                    "work" => EmailLabel.Work,
                    _ => EmailLabel.Other,
                },
                contactType),
            IsPrimary = HasPref(prop.Params),
            Value = value,
        };
    }

    private static NewPhoneNumber? ParsePhone(VCardProperty prop, ContactType contactType)
    {
        var raw = UnescapeText(prop.RawValue).Trim();
        var value = raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ? raw[4..] : raw;
        if (value.Length == 0 || !PhoneValidator.IsValid(value))
        {
            return null;
        }

        return new NewPhoneNumber
        {
            Label = ContactLabelScope.Clamp(
                ExtensionLabel<PhoneLabel>(prop.Params) ?? ExtractTypeToken(prop.Params, "home", "work", "cell", "mobile") switch
                {
                    "home" => PhoneLabel.Home,
                    "work" => PhoneLabel.Work,
                    "cell" or "mobile" => PhoneLabel.Mobile,
                    _ => PhoneLabel.Other,
                },
                contactType),
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

    /// <summary>
    /// Resolves this property's <c>X-ODYSSEY-LABEL</c> parameter, or <see langword="null"/> when it is
    /// absent or does not name a member — in which case the caller falls back to the standard
    /// <c>TYPE</c> token.
    ///
    /// <para><b>Matched against <see cref="Enum.GetNames{TEnum}()"/>, never with
    /// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/></b>: <c>TryParse</c> returns
    /// <see langword="true"/> for arbitrary numeric strings <i>and</i> for comma-separated name lists
    /// (<c>"Home,Work"</c> → ordinal 3), either of which would persist a value no member names. The
    /// parameter is fully attacker-controlled, and a value that does not match is discarded silently —
    /// never stored, echoed, logged, or reported through <c>ImportSkipCollector</c> (issue #47 §10.4,
    /// §10.7).</para>
    /// </summary>
    private static TLabel? ExtensionLabel<TLabel>(IReadOnlyDictionary<string, string> parameters)
        where TLabel : struct, Enum
    {
        if (!parameters.TryGetValue(ExtensionLabelParamName, out var raw))
        {
            return null;
        }

        var name = Enum.GetNames<TLabel>()
            .FirstOrDefault(n => string.Equals(n, raw.Trim(), StringComparison.OrdinalIgnoreCase));

        return name is null ? null : Enum.Parse<TLabel>(name);
    }

    private static bool HasPref(IReadOnlyDictionary<string, string> parameters) => parameters.ContainsKey("PREF");

    private static DateTime? ParseBirthday(string? value) => ParseVCardDate(value);

    /// <summary>
    /// A vCard date property in either basic (<c>yyyyMMdd</c>) or extended (<c>yyyy-MM-dd</c>) form.
    /// An unparseable value reads as <b>absent</b>, never as an error — the posture <c>BDAY</c>
    /// already had and which <c>DEATHDATE</c>, <c>X-ODYSSEY-ESTABLISHED</c> and
    /// <c>X-ODYSSEY-DISSOLVED</c> inherit (issue #48 §11): one malformed property must not cost the
    /// user a whole card.
    /// </summary>
    private static DateTime? ParseVCardDate(string? value)
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
}
