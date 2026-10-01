using Odyssey.Context;
using Microsoft.EntityFrameworkCore;

namespace Odyssey.Core.Finance;

/// <summary>
/// The invariants every transaction write enforces, declared once so <see cref="TransactionService"/>
/// and the file-analysis import (<see cref="FileAnalysisService.ImportCandidatesAsync"/>) cannot drift
/// apart (issue #237). The import used to build <see cref="Transaction"/> rows by hand and skipped every
/// one of these, so a foreign-currency amount was summed as if it were in the account's currency, a
/// closed account kept growing, and an unknown contact surfaced as a raw FK <c>500</c>.
/// </summary>
internal static class TransactionWriteRules
{
    public static void EnsureAccountIsOpen(Account account)
    {
        if (account.Closed is not null || account.Archived is not null)
        {
            throw new DomainValidationException("Transactions cannot be added to a closed or archived account.");
        }
    }

    public static void EnsureCurrencyMatchesAccount(Account account, string normalizedCurrencyCode)
    {
        if (account.CurrencyCode != normalizedCurrencyCode)
        {
            throw new DomainValidationException("Transaction currency must match account currency.");
        }
    }

    /// <summary>
    /// Resolves a set of requested tag ids to tracked <see cref="TransactionTag"/> entities, validating
    /// that each one exists and is not archived. Duplicate ids are de-duplicated; an empty or null
    /// request yields no tags.
    /// </summary>
    public static async Task<List<TransactionTag>> ResolveTagsAsync(
        OdysseyContext context, IEnumerable<Guid>? tagIds, CancellationToken cancellationToken = default)
    {
        var distinctIds = tagIds?.Distinct().ToList() ?? [];
        if (distinctIds.Count == 0)
        {
            return [];
        }

        var tags = await context.TransactionTags
            .Where(tag => distinctIds.Contains(tag.TransactionTagId) && tag.Archived == null)
            .ToListAsync(cancellationToken);

        var missing = distinctIds.Except(tags.Select(tag => tag.TransactionTagId)).ToList();
        if (missing.Count > 0)
        {
            throw new DomainValidationException(
                $"Transaction tag ID(s) {string.Join(", ", missing)} are invalid or archived.");
        }

        return tags;
    }

    public static async Task EnsureContactIsValidAsync(
        IContactLookup contactLookup, Guid? contactId, CancellationToken cancellationToken = default)
    {
        if (contactId is null)
        {
            return;
        }

        var refs = await contactLookup.ResolveRefsAsync([contactId.Value], cancellationToken);
        EnsureContactIsValid(refs, contactId);
    }

    /// <summary>The same check against refs the caller already resolved, so a batch pays one query.</summary>
    public static void EnsureContactIsValid(IReadOnlyDictionary<Guid, ContactRef> refs, Guid? contactId)
    {
        if (contactId is null)
        {
            return;
        }

        var isValidContact = refs.TryGetValue(contactId.Value, out var contactRef) && contactRef.Archived == null;
        if (!isValidContact)
        {
            throw new DomainValidationException($"Contact ID {contactId} is invalid or archived.");
        }
    }
}
