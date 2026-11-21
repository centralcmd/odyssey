using System.Data;
using System.Globalization;
using Odyssey.Context;
using Odyssey.Dtos.Journal;
using Odyssey.Core.Locations;
using Odyssey.Dtos;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Odyssey.Core.Journal;

public partial class ContactService
{
    // ── Alias sub-resource (issue #48 §7) ─────────────────────────────────────

    /// <summary>
    /// Case- <b>and accent</b>-insensitive comparison of two alias values. The index is
    /// <c>utf8mb4_*_ci</c>, which is insensitive to both, so an <c>OrdinalIgnoreCase</c> pre-check
    /// would pass <c>"Renee"</c> against a stored <c>"Renée"</c> and let the <i>index</i> reject it —
    /// a 500-shaped failure on an ordinary write, not just under concurrency. Setting both options
    /// makes the service, the index and the accent- and case-sensitive EF InMemory provider agree.
    ///
    /// <para>
    /// <b>This is ICU-dependent.</b> <c>IgnoreNonSpace</c> is culture-aware, so under invariant
    /// globalization accent folding silently stops — passing on a dev box and on both fast test tiers
    /// and degrading only where it matters. The API image installs <c>icu-libs</c> and a startup
    /// guard refuses to serve an invariant-globalization process (issue #48 §5, AC 42).
    /// </para>
    /// </summary>
    private static bool AliasEquals(string left, string right) =>
        string.Compare(left, right, CultureInfo.InvariantCulture,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;

    public async Task<IReadOnlyList<ExistingContactAlias>?> GetAliases(Guid contactId, CancellationToken cancellationToken = default)
    {
        if (!await context.Contacts.AnyAsync(c => c.ContactId == contactId, cancellationToken))
        {
            return null;
        }

        // Ordered by the column's own collation, then id — which is why the inline
        // ExistingContact.Aliases list (sorted in memory with OrdinalIgnoreCase) matches this only
        // modulo collation on accented input. Stated in §6 as accepted, not as a defect.
        var rows = await context.ContactAliases.AsNoTracking()
            .Where(a => a.ContactId == contactId)
            .OrderBy(a => a.Value).ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);
        return rows.Adapt<List<ExistingContactAlias>>();
    }

    public async Task<ExistingContactAlias?> CreateAlias(Guid contactId, NewContactAlias request, CancellationToken cancellationToken = default)
    {
        var contact = await LoadForChildMutation(contactId, cancellationToken);
        if (contact is null)
        {
            return null;
        }

        var value = CleanAliasValue(request.Value);
        var label = CleanAliasLabel(request.Label);

        var siblings = await context.ContactAliases.Where(a => a.ContactId == contactId).ToListAsync(cancellationToken);
        if (siblings.Any(a => AliasEquals(a.Value, value)))
        {
            throw DuplicateAlias();
        }

        if (siblings.Count >= ContactAliasRules.MaxPerContact)
        {
            throw new DomainUnprocessableException(
                $"A contact can have at most {ContactAliasRules.MaxPerContact} aliases.", "value");
        }

        var alias = new ContactAlias { ContactId = contactId, Value = value, Label = label };
        context.ContactAliases.Add(alias);
        Touch(contact);
        await SaveAliasChangeAsync(cancellationToken);
        return alias.Adapt<ExistingContactAlias>();
    }

    public async Task<bool> UpdateAlias(Guid contactId, Guid aliasId, NewContactAlias request, CancellationToken cancellationToken = default)
    {
        var contact = await LoadForChildMutation(contactId, cancellationToken);
        if (contact is null)
        {
            return false;
        }

        // Containment: the alias is resolved SCOPED to the parent, so an id belonging to another
        // contact is a 404 rather than a cross-parent write (§10.1).
        var alias = await FindChildAsync(context.ContactAliases, contactId, aliasId, cancellationToken);
        if (alias is null)
        {
            return false;
        }

        var value = CleanAliasValue(request.Value);
        var label = CleanAliasLabel(request.Label);

        var siblings = await context.ContactAliases
            .Where(a => a.ContactId == contactId && a.Id != aliasId)
            .ToListAsync(cancellationToken);
        if (siblings.Any(a => AliasEquals(a.Value, value)))
        {
            throw DuplicateAlias();
        }

        alias.Value = value;
        // A replace, not a patch: an omitted or blank label CLEARS a previously-set one, matching the
        // siblings' PUT semantics. No cap check — a replace cannot grow the collection, which is why
        // this verb has no 422.
        alias.Label = label;
        Touch(contact);
        await SaveAliasChangeAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAlias(Guid contactId, Guid aliasId, CancellationToken cancellationToken = default)
    {
        var contact = await LoadForChildMutation(contactId, cancellationToken);
        if (contact is null)
        {
            return false;
        }

        var alias = await FindChildAsync(context.ContactAliases, contactId, aliasId, cancellationToken);
        if (alias is null)
        {
            return false;
        }

        context.ContactAliases.Remove(alias);
        Touch(contact);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The duplicate-alias rejection, as one factory so the pre-check and the 1062 race path can
    /// never disagree about the message or the field (issue #48 §5 step 7, AC 11).
    /// <b>It names neither the submitted value nor the label</b> — the value is frequently a maiden
    /// name, and a response body has a wider audience than the contacts table (§10.6). This is a
    /// deliberate deviation from <c>JournalTagService.EnsureNameAvailable</c>, which does echo: right
    /// for a tag, wrong here. Do not "fix" it toward that precedent.
    /// </summary>
    private static DomainConflictException DuplicateAlias() =>
        new("This contact already has that alias.", "value");

    /// <summary>
    /// Saves an alias insert or update, translating the unique index's 1062 into the same
    /// <see cref="DuplicateAlias"/> the in-memory pre-check throws.
    ///
    /// <para>
    /// This catch is both the concurrency backstop <b>and</b> a redaction fix. Letting the
    /// <c>DbUpdateException</c> reach <c>GlobalExceptionHandler</c> would map it to the right status
    /// code but pass the exception OBJECT to the logger, serialising MariaDB's
    /// <c>Duplicate entry 'xxx-Hansen' for key …</c> — the alias value itself — into a log line.
    /// </para>
    /// </summary>
    private async Task SaveAliasChangeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKey(ex))
        {
            // The failed insert/update is still tracked; leaving it would flush on the next save.
            context.ChangeTracker.Clear();
            throw DuplicateAlias();
        }
    }

    private static string CleanAliasValue(string value)
    {
        var cleaned = MultiWhitespaceRegex.Replace((value ?? string.Empty).Trim(), " ");
        if (cleaned.Length == 0)
        {
            throw new DomainValidationException("An alias is required.", code: null, field: "value");
        }

        if (cleaned.Length > ContactAliasRules.MaxValueLength)
        {
            throw new DomainValidationException(
                $"An alias cannot exceed {ContactAliasRules.MaxValueLength} characters.", code: null, field: "value");
        }
        // Defence-in-depth behind ContactAliasRules.Pattern, which rejects the same set in model
        // validation on the HTTP path — this one covers direct, non-HTTP callers.
        if (cleaned.Any(char.IsControl))
        {
            throw new DomainValidationException(ContactAliasRules.ValueErrorMessage, code: null, field: "value");
        }

        return cleaned;
    }

    private static string? CleanAliasLabel(string? label)
    {
        if (label is null)
        {
            return null;
        }

        var cleaned = MultiWhitespaceRegex.Replace(label.Trim(), " ");
        // Empty collapses to null so "" and absent are never two states.
        if (cleaned.Length == 0)
        {
            return null;
        }

        if (cleaned.Length > ContactAliasRules.MaxLabelLength)
        {
            throw new DomainValidationException(
                $"An alias label cannot exceed {ContactAliasRules.MaxLabelLength} characters.", code: null, field: "label");
        }

        if (cleaned.Any(char.IsControl))
        {
            throw new DomainValidationException(ContactAliasRules.LabelErrorMessage, code: null, field: "label");
        }

        return cleaned;
    }

    // ── Contact-method sub-resources: addresses, emails, phones ───────────────
    // One code path per verb, shared by all three collections (issue #287 M7). Each collection supplies
    // only what differs: its DbSet, its label-scope check, and how a request becomes column values.

    /// <summary>Null when the contact does not exist; primary first, then by id. The address fields are
    /// projected from each row's linked location (issue #380), with <see cref="ExistingAddress.OtherUses"/>
    /// counted over the kinds <paramref name="claims"/> may read.</summary>
    public async Task<IReadOnlyList<ExistingAddress>?> GetAddresses(
        Guid contactId, CancellationToken cancellationToken = default, LocationClaims? claims = null)
    {
        if (!await context.Contacts.AnyAsync(c => c.ContactId == contactId, cancellationToken))
        {
            return null;
        }

        var rows = await context.Addresses.AsNoTracking()
            .Include(a => a.Location)
            .Where(a => a.ContactId == contactId)
            .OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);
        var dtos = rows.Adapt<List<ExistingAddress>>();
        await FillOtherUsesAsync(dtos, claims, cancellationToken);
        return dtos;
    }

    /// <summary>
    /// Null when the contact does not exist. The address is a link to a place (issue #380 §5.3), given by
    /// <see cref="NewAddress.LocationId"/> or found-or-created from the fields; <c>applyTo</c> is ignored, since
    /// a new address has no linked place to edit.
    /// </summary>
    /// <param name="claims">The caller's location claims; null is no capability at all (pass <see cref="LocationClaims.Trusted"/> explicitly for a trusted, non-HTTP caller).</param>
    /// <param name="userId">The acting user, attributed on a place this creates and in the log.</param>
    public async Task<ExistingAddress?> CreateAddress(
        Guid contactId, NewAddress request, CancellationToken cancellationToken = default,
        LocationClaims? claims = null, string? userId = null) =>
        (await CreateAddressCoreAsync(contactId, request, claims, userId, cancellationToken)).Address;

    /// <summary><see cref="CreateAddress"/>, plus whether a new place was created — vCard import counts it.</summary>
    internal async Task<(ExistingAddress? Address, bool PlaceCreated)> CreateAddressCoreAsync(
        Guid contactId, NewAddress request, LocationClaims? claims, string? userId, CancellationToken cancellationToken)
    {
        claims ??= LocationClaims.None;
        var contact = await LoadForChildMutation(contactId, cancellationToken);
        if (contact is null)
        {
            return (null, false);
        }

        RequireLabelValidFor(request.Label, contact.Type);
        var resolver = NewResolver();
        var location = await ResolveAddressLinkAsync(resolver, request, currentId: null, claims, userId, cancellationToken);
        var placeCreated = resolver.PendingCreates > 0;
        await EnsureNotListedAsync(contactId, location.LocationId, excludeAddressId: null, cancellationToken);

        var siblings = await context.Addresses.Where(m => m.ContactId == contactId).ToListAsync(cancellationToken);
        var created = new Address
        {
            ContactId = contactId,
            Label = request.Label,
            LocationId = location.LocationId,
            Location = location,
        };
        context.Addresses.Add(created);

        ArbitratePrimaryOnCreate(siblings, created, request.IsPrimary);
        Touch(contact);
        await SaveAddressAsync(resolver, cancellationToken);

        logger?.LogInformation(
            "Contact {ContactId} address {AddressId} linked to location {LocationId} by {ActorUserId}.",
            contactId, created.Id, created.LocationId, userId);
        var dto = created.Adapt<ExistingAddress>();
        await FillOtherUsesAsync([dto], claims, cancellationToken);
        return (dto, placeCreated);
    }

    /// <summary>
    /// False when the contact does not exist or the row is not one of its rows. A <c>locationId</c> relinks
    /// (a changed one needs <c>locations.read</c>); unchanged fields keep the link; changed fields need
    /// <see cref="NewAddress.ApplyTo"/> (issue #380 §5.3, v6–v8):
    /// <list type="bullet">
    /// <item><c>ThisRecord</c> — copy-on-write under <c>locations.create</c>; the old place is unchanged;</item>
    /// <item><c>AllUses</c> — the linked place is edited in place under <c>locations.update</c>, with
    /// <see cref="NewAddress.ExpectedOtherUses"/> rechecked under <c>FOR UPDATE</c>;</item>
    /// <item>a change of case or whitespace only (the address key is unchanged) is applied in place as
    /// <c>AllUses</c>, whatever <c>applyTo</c> says, since there is no other place to move to.</item>
    /// </list>
    /// The whole write is one transaction. A place is never deleted here.
    /// </summary>
    public async Task<bool> UpdateAddress(
        Guid contactId, Guid addressId, NewAddress request, CancellationToken cancellationToken = default,
        LocationClaims? claims = null, string? userId = null)
    {
        claims ??= LocationClaims.None;
        var found = false;
        var mode = "kept";
        Guid? linked = null;

        await LocationTransactions.ExecuteAsync(context, async () =>
        {
            found = false;
            var contact = await LoadForChildMutation(contactId, cancellationToken);
            if (contact is null)
            {
                return;
            }

            var address = await FindChildAsync(context.Addresses, contactId, addressId, cancellationToken);
            if (address is null)
            {
                return;
            }

            found = true;
            RequireLabelValidFor(request.Label, contact.Type);
            await context.Entry(address).Reference(a => a.Location).LoadAsync(cancellationToken);

            var resolver = NewResolver();
            if (request.LocationId is not null)
            {
                var location = await ResolveAddressLinkAsync(resolver, request, address.LocationId, claims, userId, cancellationToken);
                mode = location.LocationId == address.LocationId ? "kept" : "relinked";
                await EnsureNotListedAsync(contactId, location.LocationId, addressId, cancellationToken);
                address.LocationId = location.LocationId;
                address.Location = location;
            }
            else
            {
                var fields = RequiredAddressFields(request);
                var geocode = GeocodeOf(request);
                var current = address.Location;
                if (fields != LocationAddress.FromLocation(current))
                {
                    var sameKey = fields.Key is { } key && key == current.AddressKey;
                    if (sameKey || request.ApplyTo == AddressApplyTo.AllUses)
                    {
                        mode = "edited in place";
                        await resolver.EditInPlaceAsync(
                            new LocationResolver.InPlaceEdit(current.LocationId, fields, PropertyFields: false,
                                request.ExpectedOtherUses, LocationReferenceGuard.Kind.Contact),
                            claims, userId, cancellationToken);
                    }
                    else if (request.ApplyTo == AddressApplyTo.ThisRecord)
                    {
                        mode = "copied";
                        var location = (await resolver.FindOrCreateAsync(
                            fields, LocationSource.ContactAddress, userId, claims, cancellationToken, geocode)).Location;
                        await EnsureNotListedAsync(contactId, location.LocationId, addressId, cancellationToken);
                        address.LocationId = location.LocationId;
                        address.Location = location;
                    }
                    else
                    {
                        throw new DomainUnprocessableException(
                            "Say whether the new address applies to this contact only or to every use of the place.", "applyTo");
                    }
                }
            }

            address.Label = request.Label;
            var siblings = await context.Addresses.Where(m => m.ContactId == contactId).ToListAsync(cancellationToken);
            ArbitratePrimaryOnUpdate(siblings, address, request.IsPrimary);
            Touch(contact);
            await SaveAddressAsync(resolver, cancellationToken);
            linked = address.LocationId;
        }, IsolationLevel.ReadCommitted, cancellationToken);

        if (found)
        {
            logger?.LogInformation(
                "Contact {ContactId} address {AddressId} saved by {ActorUserId}: location {LocationId} {Mode}.",
                contactId, addressId, userId, linked, mode);
        }

        return found;
    }

    private LocationResolver NewResolver() => new(context, timeProvider, logger);

    /// <summary>
    /// The place a create, or a <c>locationId</c> update, links to: by id (an address-complete place; a changed
    /// link needs <c>locations.read</c>; fields sent with it must equal the place's, else <c>422</c> keyed
    /// <c>locationId</c>), or found-or-created from the fields under <c>locations.create</c>.
    /// </summary>
    private static async Task<Odyssey.Context.Location> ResolveAddressLinkAsync(
        LocationResolver resolver, NewAddress request, Guid? currentId, LocationClaims claims, string? userId,
        CancellationToken cancellationToken)
    {
        if (request.LocationId is { } id)
        {
            var location = await resolver.LinkByIdAsync(id, currentId, requireAddress: true, claims, cancellationToken);
            var sent = SentAddressFields(request);
            if (!sent.IsEmpty && sent != LocationAddress.FromLocation(location))
            {
                throw new DomainUnprocessableException(
                    "Send either a place or address fields; these fields do not match that place.", LocationResolver.LocationIdField);
            }

            return location;
        }

        var fields = RequiredAddressFields(request);
        var geocode = GeocodeOf(request);
        return (await resolver.FindOrCreateAsync(
            fields, LocationSource.ContactAddress, userId, claims, cancellationToken, geocode)).Location;
    }

    // The optional geocode (§5.3a, v9): validated whenever the fields path runs, so its 422s do not depend on
    // whether the address already exists, and applied only to a place the resolver creates.
    private static LocationGeocode? GeocodeOf(NewAddress request) => LocationGeocode.From(
        request.Latitude, request.Longitude, request.TimeZoneId, request.Source, request.SourceReference);

    private static LocationAddress SentAddressFields(NewAddress request) => LocationAddress.Of(
        request.Line1, request.Line2, request.PostalCode, request.City, request.Region, request.CountryCode);

    // The fields path (§5.3): cleaned into the stored form, the length and country shape checked as before
    // issue #380 (400s), and line 1, city and country required — a 422 keyed on the first one missing, since
    // the id path leaves them out of the body.
    private static LocationAddress RequiredAddressFields(NewAddress request)
    {
        var fields = SentAddressFields(request);
        CheckLength(fields.Line1, ContactMethodLimits.AddressLineMaxLength, "Line 1");
        CheckLength(fields.Line2, ContactMethodLimits.AddressLineMaxLength, "Line 2");
        CheckLength(fields.PostalCode, ContactMethodLimits.PostalCodeMaxLength, "Postal code");
        CheckLength(fields.City, ContactMethodLimits.CityMaxLength, "City");
        CheckLength(fields.Region, ContactMethodLimits.RegionMaxLength, "Region");
        if (fields.CountryCode is { } country && !CountryCodeRegex.IsMatch(country))
        {
            throw new DomainValidationException("Country code must be two letters.", code: null, field: "countryCode");
        }

        if (fields.FirstMissingRequiredField is { } missing)
        {
            throw new DomainUnprocessableException("Give line 1, a city and a country, or choose a place.", missing);
        }

        return fields;
    }

    private static void CheckLength(string? value, int maxLength, string label)
    {
        if (value is { } text && text.Length > maxLength)
        {
            throw new DomainValidationException($"{label} cannot exceed {maxLength} characters.");
        }
    }

    /// <summary>
    /// A contact lists a place at most once (unique <c>(ContactId, LocationId)</c>, issue #380 §5.3): a
    /// <c>409</c> keyed <c>locationId</c>.
    /// </summary>
    private async Task EnsureNotListedAsync(Guid contactId, Guid locationId, Guid? excludeAddressId, CancellationToken cancellationToken)
    {
        var listed = await context.Addresses.AnyAsync(
            a => a.ContactId == contactId && a.LocationId == locationId && a.Id != excludeAddressId,
            cancellationToken);
        if (listed)
        {
            throw ListedTwice();
        }
    }

    private static DomainConflictException ListedTwice() =>
        new("This contact already lists that address.", LocationResolver.LocationIdField);

    // The unique (ContactId, LocationId) index is the race backstop for the pre-check above.
    private static async Task SaveAddressAsync(LocationResolver resolver, CancellationToken cancellationToken)
    {
        try
        {
            await resolver.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbErrors.IsDuplicateKeyOn(ex, "IX_Addresses_ContactId_LocationId"))
        {
            throw ListedTwice();
        }
    }

    /// <summary>Fills <see cref="ExistingAddress.OtherUses"/> for every row, from one batch of counts.</summary>
    private async Task FillOtherUsesAsync(IReadOnlyCollection<ExistingAddress> dtos, LocationClaims? claims, CancellationToken cancellationToken)
    {
        if (dtos.Count == 0)
        {
            return;
        }

        claims ??= LocationClaims.None;
        var counts = await new LocationReferenceGuard(context).CountAsync(dtos.Select(d => d.LocationId).ToList(), cancellationToken);
        foreach (var dto in dtos)
        {
            dto.OtherUses = LocationReferenceGuard.OtherUses(
                counts.GetValueOrDefault(dto.LocationId) ?? LocationReferenceGuard.Counts.Zero,
                LocationReferenceGuard.Kind.Contact, claims);
        }
    }

    public Task<bool> DeleteAddress(Guid contactId, Guid addressId, CancellationToken cancellationToken = default) =>
        DeleteMethodAsync(context.Addresses, contactId, addressId, cancellationToken);

    public Task<IReadOnlyList<ExistingEmailAddress>?> GetEmails(Guid contactId, CancellationToken cancellationToken = default) =>
        GetMethodsAsync<EmailAddress, ExistingEmailAddress>(context.EmailAddresses, contactId, cancellationToken);

    public Task<ExistingEmailAddress?> CreateEmail(Guid contactId, NewEmailAddress request, CancellationToken cancellationToken = default) =>
        CreateMethodAsync<EmailAddress, ExistingEmailAddress>(context.EmailAddresses, contactId, request.IsPrimary,
            contact => RequireLabelValidFor(request.Label, contact.Type),
            () => new EmailAddress
            {
                ContactId = contactId,
                Label = request.Label,
                Value = CleanRequired(request.Value, ContactMethodLimits.EmailMaxLength, "Email address"),
            },
            cancellationToken);

    public Task<bool> UpdateEmail(Guid contactId, Guid emailId, NewEmailAddress request, CancellationToken cancellationToken = default) =>
        UpdateMethodAsync<EmailAddress>(context.EmailAddresses, contactId, emailId, request.IsPrimary,
            contact => RequireLabelValidFor(request.Label, contact.Type),
            email =>
            {
                email.Label = request.Label;
                email.Value = CleanRequired(request.Value, ContactMethodLimits.EmailMaxLength, "Email address");
            },
            cancellationToken);

    public Task<bool> DeleteEmail(Guid contactId, Guid emailId, CancellationToken cancellationToken = default) =>
        DeleteMethodAsync(context.EmailAddresses, contactId, emailId, cancellationToken);

    public Task<IReadOnlyList<ExistingPhoneNumber>?> GetPhones(Guid contactId, CancellationToken cancellationToken = default) =>
        GetMethodsAsync<PhoneNumber, ExistingPhoneNumber>(context.PhoneNumbers, contactId, cancellationToken);

    public Task<ExistingPhoneNumber?> CreatePhone(Guid contactId, NewPhoneNumber request, CancellationToken cancellationToken = default) =>
        CreateMethodAsync<PhoneNumber, ExistingPhoneNumber>(context.PhoneNumbers, contactId, request.IsPrimary,
            contact => RequireLabelValidFor(request.Label, contact.Type),
            () => new PhoneNumber
            {
                ContactId = contactId,
                Label = request.Label,
                Value = CleanRequired(request.Value, ContactMethodLimits.PhoneMaxLength, "Phone number"),
            },
            cancellationToken);

    public Task<bool> UpdatePhone(Guid contactId, Guid phoneId, NewPhoneNumber request, CancellationToken cancellationToken = default) =>
        UpdateMethodAsync<PhoneNumber>(context.PhoneNumbers, contactId, phoneId, request.IsPrimary,
            contact => RequireLabelValidFor(request.Label, contact.Type),
            phone =>
            {
                phone.Label = request.Label;
                phone.Value = CleanRequired(request.Value, ContactMethodLimits.PhoneMaxLength, "Phone number");
            },
            cancellationToken);

    public Task<bool> DeletePhone(Guid contactId, Guid phoneId, CancellationToken cancellationToken = default) =>
        DeleteMethodAsync(context.PhoneNumbers, contactId, phoneId, cancellationToken);

    /// <summary>Null when the contact does not exist; primary first, then by id.</summary>
    private async Task<IReadOnlyList<TDto>?> GetMethodsAsync<TMethod, TDto>(
        DbSet<TMethod> set, Guid contactId, CancellationToken cancellationToken)
        where TMethod : class, IContactMethod
    {
        if (!await context.Contacts.AnyAsync(c => c.ContactId == contactId, cancellationToken))
        {
            return null;
        }

        var rows = await set.AsNoTracking()
            .Where(m => m.ContactId == contactId)
            .OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);
        return rows.Adapt<List<TDto>>();
    }

    /// <summary>
    /// Null when the contact does not exist. The label scope is checked before any row is built, and the
    /// first row in a collection is primary whatever the request says (§9).
    /// </summary>
    private async Task<TDto?> CreateMethodAsync<TMethod, TDto>(
        DbSet<TMethod> set, Guid contactId, bool requestedPrimary,
        Action<Contact> requireLabelValid, Func<TMethod> build, CancellationToken cancellationToken)
        where TMethod : class, IContactMethod
        where TDto : class
    {
        var contact = await LoadForChildMutation(contactId, cancellationToken);
        if (contact is null)
        {
            return null;
        }

        requireLabelValid(contact);

        var siblings = await set.Where(m => m.ContactId == contactId).ToListAsync(cancellationToken);
        var created = build();
        set.Add(created);

        ArbitratePrimaryOnCreate(siblings, created, requestedPrimary);
        Touch(contact);
        await context.SaveChangesAsync(cancellationToken);
        return created.Adapt<TDto>();
    }

    /// <summary>
    /// False when the contact does not exist or the row is not one of ITS rows — containment: an id
    /// belonging to another contact is a 404 rather than a cross-parent write (§10.1).
    /// </summary>
    private async Task<bool> UpdateMethodAsync<TMethod>(
        DbSet<TMethod> set, Guid contactId, Guid methodId, bool requestedPrimary,
        Action<Contact> requireLabelValid, Action<TMethod> apply, CancellationToken cancellationToken)
        where TMethod : class, IContactMethod
    {
        var contact = await LoadForChildMutation(contactId, cancellationToken);
        if (contact is null)
        {
            return false;
        }

        var method = await FindChildAsync(set, contactId, methodId, cancellationToken);
        if (method is null)
        {
            return false;
        }

        requireLabelValid(contact);
        apply(method);

        var siblings = await set.Where(m => m.ContactId == contactId).ToListAsync(cancellationToken);
        ArbitratePrimaryOnUpdate(siblings, method, requestedPrimary);
        Touch(contact);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Removing the primary row promotes the first remaining one, so a primary always exists.</summary>
    private async Task<bool> DeleteMethodAsync<TMethod>(
        DbSet<TMethod> set, Guid contactId, Guid methodId, CancellationToken cancellationToken)
        where TMethod : class, IContactMethod
    {
        var contact = await LoadForChildMutation(contactId, cancellationToken);
        if (contact is null)
        {
            return false;
        }

        var method = await FindChildAsync(set, contactId, methodId, cancellationToken);
        if (method is null)
        {
            return false;
        }

        set.Remove(method);
        var remaining = await set.Where(m => m.ContactId == contactId && m.Id != methodId).ToListAsync(cancellationToken);
        PromoteFirstIfNonePrimary(remaining, method.IsPrimary);
        Touch(contact);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Resolved SCOPED to the parent, never by id alone, on every verb of every child collection.
    private static Task<TChild?> FindChildAsync<TChild>(
        DbSet<TChild> set, Guid contactId, Guid childId, CancellationToken cancellationToken)
        where TChild : class, IContactChild =>
        set.FirstOrDefaultAsync(c => c.Id == childId && c.ContactId == contactId, cancellationToken);

    // ── Shared child helpers ──────────────────────────────────────────────────

    // Loads the parent tracked so its UpdatedAt bump (§9, F4) persists in the same transaction.
    // ── Label scope (issue #47 §9) ─────────────────────────────────────────────

    /// <summary>
    /// The invariant every contact-method write holds: a label is valid for its contact's type.
    ///
    /// <para>Create and update apply the IDENTICAL check — there is no comparison against the stored
    /// value, and therefore no constraint on when <c>Update*</c> assigns the row's fields. An earlier
    /// draft compared against the stored label, which silently removed the rejection from all three
    /// <c>PUT</c>s while every <c>POST</c> test still passed.</para>
    ///
    /// <para>422, not 400: the rule is <i>derived</i> — it depends on the parent contact's
    /// <see cref="Contact.Type"/>, which model validation cannot see — so it is a well-formed request
    /// that cannot be processed, not a malformed one. The field is <c>label</c>, the same key the
    /// client's own rule uses, so one binding serves both.</para>
    /// </summary>
    private static void RequireLabelValidFor(AddressLabel label, ContactType type)
    {
        if (!ContactLabelScope.IsValidFor(label, type))
        {
            throw new DomainUnprocessableException(
                LabelScopeMessage(label, type, ContactLabelScope.AddressLabelsFor(type)), "label");
        }
    }

    /// <inheritdoc cref="RequireLabelValidFor(AddressLabel, ContactType)"/>
    private static void RequireLabelValidFor(EmailLabel label, ContactType type)
    {
        if (!ContactLabelScope.IsValidFor(label, type))
        {
            throw new DomainUnprocessableException(
                LabelScopeMessage(label, type, ContactLabelScope.EmailLabelsFor(type)), "label");
        }
    }

    /// <inheritdoc cref="RequireLabelValidFor(AddressLabel, ContactType)"/>
    private static void RequireLabelValidFor(PhoneLabel label, ContactType type)
    {
        if (!ContactLabelScope.IsValidFor(label, type))
        {
            throw new DomainUnprocessableException(
                LabelScopeMessage(label, type, ContactLabelScope.PhoneLabelsFor(type)), "label");
        }
    }

    // Names the offending label, the contact type and the valid set — all closed-vocabulary constants
    // a caller can read off the API contract, so the message discloses nothing the caller didn't send
    // or already know (issue #47 §10.5).
    private static string LabelScopeMessage<TLabel>(TLabel label, ContactType type, IReadOnlyList<TLabel> valid)
        where TLabel : struct, Enum =>
        $"Label '{label}' is not valid for {Article(type)} {type} contact. " +
        $"Valid labels: {string.Join(", ", valid)}.";

    private static string Article(ContactType type) => type == ContactType.Organization ? "an" : "a";

    /// <summary>
    /// Clamps every contact-method label onto the contact's NEW type after a type switch, in the
    /// caller's change tracker so it lands in the caller's single <c>SaveChangesAsync</c> (issue #47
    /// §5). Only reached when the type actually changed, so an ordinary <c>PUT</c> loads no child rows.
    /// </summary>
    private async Task RemapChildLabelsAsync(Contact contact, ContactType previousType, CancellationToken cancellationToken)
    {
        var newType = contact.Type;
        var addresses = await context.Addresses.Where(a => a.ContactId == contact.ContactId).ToListAsync(cancellationToken);
        var emails = await context.EmailAddresses.Where(e => e.ContactId == contact.ContactId).ToListAsync(cancellationToken);
        var phones = await context.PhoneNumbers.Where(p => p.ContactId == contact.ContactId).ToListAsync(cancellationToken);

        var addressLabelsRemapped = 0;
        foreach (var address in addresses)
        {
            var clamped = ContactLabelScope.Clamp(address.Label, newType);
            if (clamped == address.Label)
            {
                continue;
            }

            address.Label = clamped;
            addressLabelsRemapped++;
        }

        // Not "remappedEmails": CodeQL's private-data rule matches names like e.?mail and read this count as an address.
        var mailLabelsRemapped = 0;
        foreach (var email in emails)
        {
            var clamped = ContactLabelScope.Clamp(email.Label, newType);
            if (clamped == email.Label)
            {
                continue;
            }

            email.Label = clamped;
            mailLabelsRemapped++;
        }

        var phoneLabelsRemapped = 0;
        foreach (var phone in phones)
        {
            var clamped = ContactLabelScope.Clamp(phone.Label, newType);
            if (clamped == phone.Label)
            {
                continue;
            }

            phone.Label = clamped;
            phoneLabelsRemapped++;
        }

        // A bulk mutation with no user-facing surface (the contact PUT returns no body) needs a record
        // of itself — PII-free: ids and counts only, never a name or a value (issue #47 §10.8).
        logger?.LogInformation(
            "Contact {ContactId} type changed {PreviousType} -> {NewType}; contact-method labels remapped to Other: "
            + "{RemappedAddresses} of {AddressCount} addresses, {RemappedEmails} of {EmailCount} emails, "
            + "{RemappedPhones} of {PhoneCount} phone numbers.",
            contact.ContactId, previousType, newType,
            addressLabelsRemapped, addresses.Count, mailLabelsRemapped, emails.Count, phoneLabelsRemapped, phones.Count);
    }

    private async Task<Contact?> LoadForChildMutation(Guid contactId, CancellationToken cancellationToken) =>
        await context.Contacts.FirstOrDefaultAsync(c => c.ContactId == contactId, cancellationToken);

    private void Touch(Contact contact) => contact.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

    // On create: if requested primary — or this is the first record in the collection — make it the
    // sole primary; otherwise leave the existing primary untouched (§9).
    private static void ArbitratePrimaryOnCreate<T>(IReadOnlyList<T> siblings, T created, bool requestedPrimary)
        where T : IContactMethod
    {
        var makePrimary = requestedPrimary || siblings.Count == 0;
        created.IsPrimary = makePrimary;
        if (makePrimary)
        {
            foreach (var sibling in siblings)
            {
                sibling.IsPrimary = false;
            }
        }
    }

    // On update: setting primary clears the others; clearing it re-promotes the first sibling if the
    // collection would otherwise have no primary (§9).
    private static void ArbitratePrimaryOnUpdate<T>(IReadOnlyList<T> collection, T updated, bool requestedPrimary)
        where T : IContactMethod
    {
        if (requestedPrimary)
        {
            foreach (var item in collection)
            {
                item.IsPrimary = item.Id == updated.Id;
            }

            return;
        }

        updated.IsPrimary = false;
        if (!collection.Any(item => item.IsPrimary))
        {
            var first = collection.OrderBy(item => item.Id).FirstOrDefault();
            if (first is not null)
            {
                first.IsPrimary = true;
            }
        }
    }

    private static void PromoteFirstIfNonePrimary<T>(IReadOnlyList<T> remaining, bool removedWasPrimary)
        where T : IContactMethod
    {
        if (!removedWasPrimary || remaining.Count == 0 || remaining.Any(item => item.IsPrimary))
        {
            return;
        }

        remaining.OrderBy(item => item.Id).First().IsPrimary = true;
    }
}
