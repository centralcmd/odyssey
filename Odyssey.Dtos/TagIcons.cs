using System.Collections.Frozen;

namespace Odyssey.Dtos;

/// <summary>One selectable tag icon: a Material Icons ligature key and its human label.</summary>
public sealed record TagIconOption(string Key, string Label);

/// <summary>
/// What the ordering and display-icon rules read from a tag, whichever of the four tag types it is
/// (issue #345). <paramref name="Name"/> is nullable so a link whose tag row is missing — possible on the
/// FK-free EF InMemory tier — still orders deterministically instead of throwing.
/// </summary>
public sealed record TagIconCandidate(Guid Id, string? Name, string? Icon, string? Color);

/// <summary>
/// What a record shows for its tags (issues #279, #392): the winning tag's icon and that SAME tag's colour,
/// so the two can never come from different tags. <see cref="Color"/> is already normalised.
/// </summary>
public sealed record TagDisplay(string Icon, string? Color);

/// <summary>
/// The tag icon catalogue and the display-icon rule, shared by transaction, journal, task and photo tags
/// (issues #279, #345).
/// </summary>
/// <remarks>
/// <b>This is the single declaration, not a server rule the client re-implements.</b> It lives in
/// <c>Odyssey.Dtos</c>, which holds zero project references and is reachable from both the API and the
/// WASM client, so the write-path validator, the read-path projections, the record display icons and the
/// client picker all name one symbol — the <c>ContractPartyRoleMatrix</c> precedent.
///
/// <para>
/// <b>A key is a wire contract.</b> It is persisted in the four tag tables' <c>Icon</c> columns, so a key
/// may be <em>removed</em> (rows still holding it project as <c>null</c>, the default, and no read fails)
/// but a removed key is never re-assigned to a different meaning — that would silently re-label old rows.
/// Treat a retired key as a permanent hole, the same convention enum ordinals follow. Every key must be a
/// ligature in the bundled Material Icons font; a fixture test in <c>Odyssey.Client.Tests</c> checks that.
/// </para>
///
/// <para>
/// <see cref="Default"/> is <b>not</b> a member of <see cref="All"/> and is never stored: a request
/// selecting the default sends <c>null</c>, so each state has one representation.
/// </para>
/// </remarks>
public static class TagIcons
{
    /// <summary>The generic tag symbol, drawn for a tag with no icon and for a record with no iconned tag.</summary>
    public const string Default = "local_offer";

    /// <summary>The longest key the columns (<c>varchar(64)</c>) and the write DTOs accept.</summary>
    public const int MaxKeyLength = 64;

    /// <summary>The one message every icon rejection carries. It never echoes the submitted value.</summary>
    public const string InvalidIconMessage = "Not an available icon.";

    /// <summary>The selectable icons, in picker order. <c>more_horiz</c> ("Other") stays last.</summary>
    public static IReadOnlyList<TagIconOption> All { get; } =
    [
        new("shopping_cart", "Groceries"),
        new("restaurant", "Dining"),
        new("dinner_dining", "Dinner"),
        new("fastfood", "Takeaway"),
        new("bakery_dining", "Bakery"),
        new("local_bar", "Bar"),
        new("wine_bar", "Wine"),
        new("local_cafe", "Café"),
        new("directions_car", "Car"),
        new("car_rental", "Car rental"),
        new("local_gas_station", "Fuel"),
        new("ev_station", "EV charging"),
        new("local_parking", "Parking"),
        new("directions_bus", "Public transport"),
        new("train", "Train"),
        new("local_taxi", "Taxi"),
        new("directions_boat", "Ferry"),
        new("pedal_bike", "Bicycle"),
        new("flight", "Travel"),
        new("luggage", "Luggage"),
        new("hotel", "Hotel"),
        new("map", "Sightseeing"),
        new("home", "Housing"),
        new("bolt", "Utilities"),
        new("wifi", "Internet"),
        new("phone_iphone", "Phone"),
        new("medical_services", "Health"),
        new("local_pharmacy", "Pharmacy"),
        new("spa", "Spa"),
        new("content_cut", "Haircut"),
        new("fitness_center", "Fitness"),
        new("school", "Education"),
        new("child_care", "Children"),
        new("pets", "Pets"),
        new("checkroom", "Clothing"),
        new("movie", "Entertainment"),
        new("confirmation_number", "Tickets"),
        new("mic", "Concert"),
        new("theater_comedy", "Theatre"),
        new("festival", "Festival"),
        new("stadium", "Stadium"),
        new("museum", "Museum"),
        new("attractions", "Amusement park"),
        new("nightlife", "Nightlife"),
        new("sports_esports", "Gaming"),
        new("subscriptions", "Subscriptions"),
        new("card_giftcard", "Gifts"),
        new("volunteer_activism", "Charity"),
        new("savings", "Savings"),
        new("trending_up", "Investments"),
        new("candlestick_chart", "Stocks"),
        new("currency_bitcoin", "Crypto"),
        new("account_balance", "Bank"),
        new("account_balance_wallet", "Wallet"),
        new("local_atm", "Cash"),
        new("credit_card", "Card"),
        new("currency_exchange", "Currency exchange"),
        new("percent", "Interest"),
        new("handshake", "Loan"),
        new("receipt_long", "Bills"),
        new("request_quote", "Tax"),
        new("payments", "Salary"),
        new("elderly", "Pension"),
        new("sell", "Sales"),
        new("real_estate_agent", "Rent"),
        new("work", "Work"),
        new("build", "Repairs"),
        new("shield", "Insurance"),
        // Issue #345: journal, task and photo subjects.
        new("favorite", "Love"),
        new("mood", "Happy"),
        new("sentiment_dissatisfied", "Sad"),
        new("self_improvement", "Wellbeing"),
        new("lightbulb", "Ideas"),
        new("menu_book", "Reading"),
        new("music_note", "Music"),
        new("palette", "Art"),
        new("sports_soccer", "Sports"),
        new("photo_camera", "Photography"),
        new("event", "Event"),
        new("celebration", "Celebration"),
        new("cake", "Birthday"),
        new("family_restroom", "Family"),
        new("group", "Friends"),
        new("person", "Personal"),
        new("beach_access", "Holiday"),
        new("pool", "Pool"),
        new("sailing", "Sailing"),
        new("surfing", "Surfing"),
        new("downhill_skiing", "Skiing"),
        new("park", "Nature"),
        new("hiking", "Hiking"),
        new("holiday_village", "Camping"),
        new("rv_hookup", "Camper"),
        new("cabin", "Cabin"),
        new("outdoor_grill", "Barbecue"),
        new("forest", "Forest"),
        new("kayaking", "Kayaking"),
        new("landscape", "Landscape"),
        new("location_city", "City"),
        new("restaurant_menu", "Recipes"),
        new("flag", "Goal"),
        new("priority_high", "Urgent"),
        new("schedule", "Deadline"),
        new("checklist", "Checklist"),
        new("alarm", "Reminder"),
        new("cleaning_services", "Cleaning"),
        new("local_laundry_service", "Laundry"),
        new("yard", "Garden"),
        new("shopping_bag", "Shopping"),
        new("more_horiz", "Other"),
    ];

    private static readonly FrozenDictionary<string, TagIconOption> ByKey =
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
    /// The canonical tag order: by name, <see cref="StringComparison.OrdinalIgnoreCase"/>, ties broken by
    /// id; a nameless candidate sorts after every named one. The order of a record's tags and the walk in
    /// <see cref="Resolve(IEnumerable{TagIconCandidate}?)"/> both use this, so the first chip shown is the
    /// tag whose icon is picked.
    /// </summary>
    public static IComparer<TagIconCandidate> Comparer { get; } = new CandidateComparer();

    /// <summary>The candidates as a new list in <see cref="Comparer"/> order.</summary>
    public static List<TagIconCandidate> Order(IEnumerable<TagIconCandidate>? tags) =>
        Order(tags, tag => tag);

    /// <summary>
    /// Any tag shape as a new list in <see cref="Comparer"/> order, read through
    /// <paramref name="candidate"/>. The one sort every tag ordering in the solution goes through.
    /// </summary>
    public static List<T> Order<T>(IEnumerable<T>? tags, Func<T, TagIconCandidate> candidate)
    {
        var ordered = tags?.Where(tag => tag is not null).ToList() ?? [];
        ordered.Sort((x, y) => Comparer.Compare(candidate(x), candidate(y)));
        return ordered;
    }

    /// <summary>
    /// The display-icon rule (issue #279 §3): walk the tags in <see cref="Comparer"/> order and return the
    /// first known icon; with none, <see cref="Default"/>. Archived tags take part. Never <c>null</c>.
    /// </summary>
    public static string Resolve(IEnumerable<TagIconCandidate>? tags) =>
        Order(tags).Select(tag => tag.Icon).FirstOrDefault(IsKnown) ?? Default;

    /// <summary>
    /// The display rule with colour (issue #392): the winner is the first tag in <see cref="Comparer"/>
    /// order with a known icon — unchanged from <see cref="Resolve(IEnumerable{TagIconCandidate}?)"/> — and
    /// its colour comes along, through <see cref="HexColor.Normalize"/>. EF projections hand the raw column
    /// in (normalisation cannot run inside a <c>Select</c>), so THIS is the normalisation point for every
    /// record shape. With no winner: <see cref="Default"/> and no colour.
    /// </summary>
    public static TagDisplay ResolveDisplay(IEnumerable<TagIconCandidate>? tags)
    {
        var winner = Order(tags).FirstOrDefault(tag => IsKnown(tag.Icon));
        return winner is null ? new TagDisplay(Default, null) : new TagDisplay(winner.Icon!, HexColor.Normalize(winner.Color));
    }

    /// <summary><see cref="ResolveDisplay(IEnumerable{TagIconCandidate}?)"/> over any tag shape.</summary>
    public static TagDisplay ResolveDisplay<T>(IEnumerable<T>? tags, Func<T, TagIconCandidate> candidate) =>
        ResolveDisplay(tags?.Where(tag => tag is not null).Select(candidate));

    /// <summary><see cref="Resolve(IEnumerable{TagIconCandidate}?)"/> over any tag shape.</summary>
    public static string Resolve<T>(IEnumerable<T>? tags, Func<T, TagIconCandidate> candidate) =>
        Resolve(tags?.Where(tag => tag is not null).Select(candidate));

    private sealed class CandidateComparer : IComparer<TagIconCandidate>
    {
        public int Compare(TagIconCandidate? x, TagIconCandidate? y)
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

            if (x.Name is null != y.Name is null)
            {
                return x.Name is null ? 1 : -1;
            }

            var byName = StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name);
            return byName != 0 ? byName : x.Id.CompareTo(y.Id);
        }
    }
}
