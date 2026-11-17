using System.Collections.Frozen;

namespace Odyssey.Dtos;

/// <summary>
/// One selectable tag icon: a Material Icons ligature key, its human label, and space-separated search
/// keywords (synonyms the picker matches beside the label; never stored, never on the wire).
/// </summary>
public sealed record TagIconOption(string Key, string Label, string Keywords = "");

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
/// (issues #279, #345). Photo albums pick from the same catalogue, with their own default
/// (<see cref="PhotoAlbumIcons"/>, issue #404).
/// </summary>
/// <remarks>
/// <b>This is the single declaration, not a server rule the client re-implements.</b> It lives in
/// <c>Odyssey.Dtos</c>, which holds zero project references and is reachable from both the API and the
/// WASM client, so the write-path validator, the read-path projections, the record display icons and the
/// client picker all name one symbol — the <c>ContractPartyRoleMatrix</c> precedent.
///
/// <para>
/// <b>A key is a wire contract.</b> It is persisted in the four tag tables' <c>Icon</c> columns and in <c>PhotoAlbums.Icon</c> (issue #404), so a key
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

    /// <summary>
    /// The selectable icons, in picker order. <c>more_horiz</c> ("Other") stays last. Each carries search
    /// keywords — synonyms the picker's search box matches beside the label; they are never stored.
    /// </summary>
    public static IReadOnlyList<TagIconOption> All { get; } =
    [
        new("shopping_cart", "Groceries", "food supermarket grocery market store"),
        new("restaurant", "Dining", "restaurant eat food meal out"),
        new("dinner_dining", "Dinner", "meal evening pasta food"),
        new("lunch_dining", "Lunch", "meal burger sandwich food midday"),
        new("fastfood", "Takeaway", "fast food burger take out delivery"),
        new("local_pizza", "Pizza", "food takeaway italian"),
        new("bakery_dining", "Bakery", "bread pastry croissant cake"),
        new("icecream", "Ice cream", "dessert sweet summer treat"),
        new("local_bar", "Bar", "drink cocktail pub alcohol"),
        new("wine_bar", "Wine", "drink alcohol glass"),
        new("local_cafe", "Café", "coffee tea cafe cup espresso"),
        new("directions_car", "Car", "vehicle auto drive automobile"),
        new("electric_car", "Electric car", "ev vehicle tesla auto"),
        new("car_rental", "Car rental", "hire vehicle rent"),
        new("two_wheeler", "Motorcycle", "motorbike scooter moped bike"),
        new("local_gas_station", "Fuel", "petrol gas diesel station"),
        new("ev_station", "EV charging", "electric charge charger power"),
        new("local_parking", "Parking", "park car garage"),
        new("garage", "Garage", "car storage parking"),
        new("directions_bus", "Public transport", "bus commute transit tram metro subway"),
        new("train", "Train", "rail railway commute subway"),
        new("local_taxi", "Taxi", "cab uber ride"),
        new("directions_boat", "Ferry", "boat ship"),
        new("local_shipping", "Delivery", "shipping truck freight parcel post moving"),
        new("pedal_bike", "Bicycle", "bike cycle cycling"),
        new("flight", "Travel", "flight plane airplane trip journey fly abroad airport"),
        new("luggage", "Luggage", "suitcase bag baggage trip travel"),
        new("hotel", "Hotel", "accommodation stay bed lodging motel"),
        new("map", "Sightseeing", "map tour tourist"),
        new("travel_explore", "Exploring", "explore discover world travel adventure globe"),
        new("public", "World", "globe international earth abroad global"),
        new("home", "Housing", "home house mortgage residence"),
        new("house", "House", "home building residence property"),
        new("cottage", "Cottage", "cabin hut holiday home summer house"),
        new("cabin", "Cabin", "hut cottage lodge holiday home mountain"),
        new("chalet", "Chalet", "cabin mountain ski lodge alpine"),
        new("villa", "Villa", "house holiday home vacation mansion"),
        new("apartment", "Apartment", "flat condo building residence"),
        new("houseboat", "Houseboat", "boat house floating"),
        new("construction", "Renovation", "construction building remodel diy tools"),
        new("chair", "Furniture", "chair sofa interior"),
        new("key", "Keys", "key lock access door"),
        new("bolt", "Utilities", "electricity power energy electric"),
        new("water_drop", "Water", "water drop utility plumbing"),
        new("local_fire_department", "Heating", "fire heat warmth stove"),
        new("solar_power", "Solar", "solar panel energy sun power"),
        new("wifi", "Internet", "wifi broadband network online"),
        new("phone_iphone", "Phone", "mobile cell smartphone telephone"),
        new("laptop", "Computer", "laptop pc tech electronics"),
        new("tv", "TV", "television streaming screen"),
        new("headphones", "Audio", "headphones podcast audiobook listen"),
        new("medical_services", "Health", "medical doctor healthcare"),
        new("local_hospital", "Hospital", "clinic emergency medical"),
        new("local_pharmacy", "Pharmacy", "medicine drugs chemist prescription"),
        new("vaccines", "Vaccination", "vaccine shot injection jab"),
        new("spa", "Spa", "wellness massage relax"),
        new("content_cut", "Haircut", "hair barber hairdresser salon scissors"),
        new("fitness_center", "Fitness", "gym workout exercise training weights"),
        new("directions_run", "Running", "run jog jogging exercise"),
        new("directions_walk", "Walking", "walk stroll hike"),
        new("school", "Education", "school university study course learning"),
        new("child_care", "Children", "kids baby child childcare daycare"),
        new("toys", "Toys", "toy play kids children"),
        new("pets", "Pets", "pet dog cat animal vet"),
        new("checkroom", "Clothing", "clothes fashion apparel wardrobe"),
        new("movie", "Entertainment", "movie film cinema"),
        new("confirmation_number", "Tickets", "ticket admission pass"),
        new("mic", "Concert", "music live gig singing microphone"),
        new("theater_comedy", "Theatre", "theater play drama comedy show"),
        new("festival", "Festival", "fair event tent"),
        new("stadium", "Stadium", "arena sports match game"),
        new("museum", "Museum", "gallery exhibition culture"),
        new("attractions", "Amusement park", "theme park fun fair ride rollercoaster"),
        new("nightlife", "Nightlife", "club party night disco"),
        new("sports_esports", "Gaming", "games video game console controller"),
        new("subscriptions", "Subscriptions", "subscription streaming membership recurring"),
        new("card_giftcard", "Gifts", "gift present christmas"),
        new("volunteer_activism", "Charity", "donation donate giving volunteer"),
        new("savings", "Savings", "save piggy bank"),
        new("trending_up", "Investments", "invest investment growth return"),
        new("candlestick_chart", "Stocks", "shares stock market trading equity"),
        new("currency_bitcoin", "Crypto", "bitcoin cryptocurrency coin"),
        new("account_balance", "Bank", "bank finance institution"),
        new("account_balance_wallet", "Wallet", "wallet money purse"),
        new("local_atm", "Cash", "money atm withdrawal notes"),
        new("credit_card", "Card", "credit debit card payment"),
        new("currency_exchange", "Currency exchange", "exchange foreign currency fx"),
        new("percent", "Interest", "interest rate percent"),
        new("handshake", "Loan", "loan deal agreement debt borrow"),
        new("receipt_long", "Bills", "bill invoice receipt expense"),
        new("request_quote", "Tax", "tax taxes quote"),
        new("gavel", "Legal", "law lawyer court contract"),
        new("description", "Documents", "document paper file contract"),
        new("payments", "Salary", "salary income pay wage paycheck"),
        new("elderly", "Pension", "pension retirement retired senior"),
        new("sell", "Sales", "sale sell price tag"),
        new("storefront", "Store", "shop store retail business"),
        new("real_estate_agent", "Rent", "rent rental landlord lease"),
        new("work", "Work", "work job office business briefcase"),
        new("build", "Repairs", "repair fix maintenance tools"),
        new("shield", "Insurance", "insurance protection security"),
        new("contacts", "Contacts", "contact address book people phonebook"),
        new("badge", "ID", "identity id card passport license"),
        new("mail", "Mail", "email letter post message"),
        new("newspaper", "News", "news newspaper article press"),
        new("favorite", "Love", "heart love romance"),
        new("mood", "Happy", "happy smile joy good"),
        new("sentiment_dissatisfied", "Sad", "sad unhappy down bad"),
        new("self_improvement", "Wellbeing", "meditation yoga mindfulness wellbeing"),
        new("lightbulb", "Ideas", "idea inspiration thought"),
        new("menu_book", "Reading", "book read books"),
        new("local_library", "Library", "library books borrow"),
        new("music_note", "Music", "music song"),
        new("palette", "Art", "art paint painting drawing creative"),
        new("photo_camera", "Photography", "photo camera picture"),
        new("star", "Favourite", "star favorite best highlight"),
        new("emoji_events", "Achievement", "trophy award prize win milestone"),
        new("sports_soccer", "Sports", "sport football soccer ball"),
        new("sports_tennis", "Tennis", "tennis racket sport padel"),
        new("golf_course", "Golf", "golf sport"),
        new("event", "Event", "event calendar date appointment"),
        new("celebration", "Celebration", "party celebrate confetti"),
        new("cake", "Birthday", "birthday cake party anniversary"),
        new("family_restroom", "Family", "family parents relatives"),
        new("group", "Friends", "friends group people social"),
        new("person", "Personal", "person me self individual"),
        new("beach_access", "Holiday", "holiday vacation beach umbrella palm tropical"),
        new("sunny", "Summer", "sun sunny summer weather vacation warm"),
        new("waves", "Sea", "ocean sea beach waves coast water"),
        new("pool", "Pool", "swimming swim pool"),
        new("scuba_diving", "Diving", "scuba dive diving snorkel snorkeling underwater"),
        new("sailing", "Sailing", "sail sailboat boat yacht"),
        new("surfing", "Surfing", "surf beach wave"),
        new("kayaking", "Kayaking", "kayak canoe paddle"),
        new("downhill_skiing", "Skiing", "ski winter snow slope"),
        new("snowboarding", "Snowboarding", "snowboard winter snow"),
        new("ac_unit", "Winter", "snow cold snowflake frost"),
        new("park", "Nature", "nature tree park outdoors"),
        new("forest", "Forest", "forest woods trees"),
        new("landscape", "Landscape", "mountain mountains scenery view"),
        new("hiking", "Hiking", "hike trek trail mountain walk"),
        new("holiday_village", "Camping", "camping tent campsite"),
        new("rv_hookup", "Camper", "camper rv caravan motorhome"),
        new("outdoor_grill", "Barbecue", "bbq grill cookout"),
        new("local_florist", "Flowers", "flower florist bouquet plant"),
        new("yard", "Garden", "garden yard plants gardening"),
        new("location_city", "City", "city town urban"),
        new("restaurant_menu", "Recipes", "recipe cooking cook food"),
        new("flag", "Goal", "goal target objective"),
        new("priority_high", "Urgent", "urgent important priority"),
        new("schedule", "Deadline", "deadline time clock due"),
        new("checklist", "Checklist", "checklist todo list tasks"),
        new("task_alt", "Done", "done complete finished check"),
        new("alarm", "Reminder", "reminder alarm notify"),
        new("cleaning_services", "Cleaning", "cleaning clean chores"),
        new("local_laundry_service", "Laundry", "laundry washing machine clothes chores"),
        new("shopping_bag", "Shopping", "shopping bag purchase buy"),
        new("more_horiz", "Other", "other misc miscellaneous"),
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
