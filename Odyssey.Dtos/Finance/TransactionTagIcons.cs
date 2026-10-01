using System.Collections.Frozen;

namespace Odyssey.Dtos.Finance;

/// <summary>One selectable transaction-tag icon: a Material Icons ligature key and its human label.</summary>
public sealed record TransactionTagIconOption(string Key, string Label);

/// <summary>
/// The transaction-tag icon catalogue and the display-icon rule (issue #279).
/// </summary>
/// <remarks>
/// <b>This is the single declaration, not a server rule the client re-implements.</b> It lives in
/// <c>Odyssey.Dtos</c>, which holds zero project references and is reachable from both the API and the
/// WASM client, so the write-path validator, the read-path projection, the transaction display icon and
/// the client picker all name one symbol — the <see cref="ContractPartyRoleMatrix"/> precedent.
///
/// <para>
/// <b>A key is a wire contract.</b> It is persisted in <c>TransactionTags.Icon</c>, so a key may be
/// <em>removed</em> (rows still holding it project as <c>null</c>, the default, and no read fails) but a
/// removed key is never re-assigned to a different meaning — that would silently re-label old rows. Treat
/// a retired key as a permanent hole, the same convention enum ordinals follow.
/// </para>
///
/// <para>
/// <see cref="Default"/> is <b>not</b> a member of <see cref="All"/> and is never stored: a request
/// selecting the default sends <c>null</c>, so each state has one representation.
/// </para>
/// </remarks>
public static class TransactionTagIcons
{
    /// <summary>The generic tag symbol, drawn for a tag with no icon and for a transaction with no iconned tag.</summary>
    public const string Default = "local_offer";

    /// <summary>The longest key the column (<c>varchar(64)</c>) and the write DTO accept.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>The one message every icon rejection carries. It never echoes the submitted value.</summary>
    public const string InvalidIconMessage = "Not an available icon.";

    /// <summary>The selectable icons, in picker order.</summary>
    public static IReadOnlyList<TransactionTagIconOption> All { get; } =
    [
        new("shopping_cart", "Groceries"),
        new("restaurant", "Dining"),
        new("local_cafe", "Café"),
        new("directions_car", "Car"),
        new("local_gas_station", "Fuel"),
        new("directions_bus", "Public transport"),
        new("flight", "Travel"),
        new("home", "Housing"),
        new("bolt", "Utilities"),
        new("wifi", "Internet"),
        new("phone_iphone", "Phone"),
        new("medical_services", "Health"),
        new("fitness_center", "Fitness"),
        new("school", "Education"),
        new("child_care", "Children"),
        new("pets", "Pets"),
        new("checkroom", "Clothing"),
        new("movie", "Entertainment"),
        new("subscriptions", "Subscriptions"),
        new("card_giftcard", "Gifts"),
        new("volunteer_activism", "Charity"),
        new("savings", "Savings"),
        new("trending_up", "Investments"),
        new("account_balance", "Bank"),
        new("credit_card", "Card"),
        new("receipt_long", "Bills"),
        new("request_quote", "Tax"),
        new("payments", "Salary"),
        new("work", "Work"),
        new("build", "Repairs"),
        new("shield", "Insurance"),
        new("more_horiz", "Other"),
    ];

    private static readonly FrozenDictionary<string, TransactionTagIconOption> ByKey =
        All.ToFrozenDictionary(option => option.Key, StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="key"/> is a selectable icon. Ordinal and case-sensitive — ligatures are
    /// lower-case — and <see cref="Default"/> is not a member.
    /// </summary>
    public static bool IsKnown(string? key) => key is not null && ByKey.ContainsKey(key);

    /// <summary>The read-side projection of a stored key: a known key as-is, anything else <c>null</c> (the default).</summary>
    public static string? Normalize(string? key) => IsKnown(key) ? key : null;

    /// <summary>The glyph to draw for a tag's icon: the key when known, otherwise <see cref="Default"/>.</summary>
    public static string Glyph(string? key) => IsKnown(key) ? key! : Default;

    /// <summary>The human label for a key; <c>"Default"</c> for <c>null</c> or an unknown key.</summary>
    public static string LabelFor(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var option) ? option.Label : "Default";

    /// <summary>
    /// The canonical transaction-tag order: by name, <see cref="StringComparison.OrdinalIgnoreCase"/>, ties
    /// broken by id. Both the order of <c>ExistingTransaction.TransactionTags</c> and the walk in
    /// <see cref="Resolve"/> use this, so the first chip shown is the tag whose icon is picked.
    /// </summary>
    public static IComparer<ExistingTransactionTag> Comparer { get; } = new TagComparer();

    /// <summary>The tags as a new list in <see cref="Comparer"/> order.</summary>
    public static List<ExistingTransactionTag> Order(IEnumerable<ExistingTransactionTag>? tags)
    {
        var ordered = tags?.Where(tag => tag is not null).ToList() ?? [];
        ordered.Sort(Comparer);
        return ordered;
    }

    /// <summary>
    /// The display-icon rule (issue #279 §3): walk the tags in <see cref="Comparer"/> order and return the
    /// first known icon; with none, <see cref="Default"/>. Archived tags take part. Never <c>null</c>.
    /// </summary>
    public static string Resolve(IEnumerable<ExistingTransactionTag>? tags) =>
        Order(tags).Select(tag => tag.Icon).FirstOrDefault(IsKnown) ?? Default;

    private sealed class TagComparer : IComparer<ExistingTransactionTag>
    {
        public int Compare(ExistingTransactionTag? x, ExistingTransactionTag? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var byName = StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name);
            return byName != 0 ? byName : x.TransactionTagId.CompareTo(y.TransactionTagId);
        }
    }
}
