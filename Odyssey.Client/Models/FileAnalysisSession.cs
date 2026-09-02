using System.Globalization;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;

namespace Odyssey.Client.Models;

/// <summary>The step the analyze-file dialog is showing.</summary>
public enum FileAnalysisPhase
{
    Consent,
    Analyzing,
    Matching,
    Blocked,
    Disabled,
    Failed,
    Empty,
    Review,
    Done,
    ReanalyzeConfirm,
    ResumeLoading,
    NoLongerAvailable,
}

/// <summary>A sub-threshold match shown as a suggestion chip (name + confidence) rather than auto-linked.</summary>
public sealed record FileAnalysisMatchSuggestion(string Name, decimal? Confidence);

/// <summary>One reviewable candidate transaction, as edited in the review grid.</summary>
public sealed class FileAnalysisRow
{
    public Guid CandidateId { get; init; }
    public bool Selected { get; set; }
    public DateTime TransactionDate { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Merchant { get; set; } = string.Empty;
    public Guid? ContactId { get; set; }
    public string? CategoryHint { get; set; }
    public List<string> TagIds { get; set; } = [];
    public decimal Amount { get; set; }

    /// <summary>
    /// What is IN the amount cell. Held separately from <see cref="Amount"/> so a partial entry
    /// ("12.", "-") survives a re-render instead of being rewritten by the number's own formatting;
    /// it falls back to the formatted amount until the cell is first edited.
    /// </summary>
    public string AmountText
    {
        get => amountText ?? FileAnalysisSession.FormatAmount(Amount);
        set => amountText = value;
    }

    private string? amountText;

    public string Currency { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public decimal? LlmConfidence { get; init; }

    // ── Match metadata (issue #266) ──
    public OdsMatchState MerchantSource { get; set; } = OdsMatchState.None;
    public decimal? MerchantConf { get; set; }
    public FileAnalysisMatchSuggestion? MerchantSuggestion { get; set; }
    public Guid? MerchantSuggestionId { get; set; }

    public OdsMatchState CatSource { get; set; } = OdsMatchState.None;
    public decimal? CatConf { get; set; }
    public FileAnalysisMatchSuggestion? CatSuggestion { get; set; }
    public List<string> CatSuggestionIds { get; set; } = [];
}

/// <summary>
/// The state machine and editing rules behind <c>FileAnalysisDialog</c>: the phase, the candidate
/// rows, how a job's persisted match data becomes (or doesn't become) a link, and the optimistic
/// create-a-merchant-then-reconcile-or-roll-back dance.
/// </summary>
/// <remarks>
/// Plain C# on purpose (issue #373). None of this needs a renderer, a DI container or an
/// <c>HttpClient</c>, and while it lived inside the dialog's 960-line <c>@code</c> block none of it
/// could be tested — including the rollback path, which is the one place a failed create can leave a
/// row pointing at a contact that does not exist. The dialog keeps the I/O: it calls the API,
/// toasts, moves focus, and asks this class what to render.
/// </remarks>
public sealed class FileAnalysisSession
{
    /// <summary>The step being shown.</summary>
    public FileAnalysisPhase Phase { get; private set; } = FileAnalysisPhase.Consent;

    /// <summary>The modal's title for the current phase.</summary>
    public string Title { get; private set; } = PhaseText(FileAnalysisPhase.Consent).Title;

    /// <summary>The modal's subtitle for the current phase.</summary>
    public string Subtitle { get; private set; } = PhaseText(FileAnalysisPhase.Consent).Subtitle;

    /// <summary>
    /// A transient announcement for a discrete in-Review action (apply/dismiss a suggestion). Takes
    /// precedence over the phase message on the dialog's polite live region; cleared on any phase change.
    /// </summary>
    public string? ActionAnnounce { get; private set; }

    /// <summary>The loaded analysis job, once one exists.</summary>
    public ExistingFileAnalysisJob? Job { get; set; }

    /// <summary>
    /// The match step's outcome (issue #266) — orthogonal to extraction; drives the per-cell
    /// suggestions and the non-blocking degraded notice, never gates the import.
    /// </summary>
    public FileAnalysisMatchStatus MatchStatus { get; set; } = FileAnalysisMatchStatus.NotRun;

    /// <summary>The reviewable candidate rows.</summary>
    public List<FileAnalysisRow> Rows { get; } = [];

    /// <summary>
    /// Whether the reviewer holds <c>contacts.create</c>. The inline "Create …" affordance is hidden
    /// without it, so a User-role reviewer never meets a 403 on a happy-path control (the server-side
    /// <c>[Authorize]</c> is the actual gate).
    /// </summary>
    public bool CanCreateContact { get; set; }

    // ── Vocabulary ────────────────────────────────────────────────────────────
    private readonly List<ExistingContact> contacts = [];
    private readonly List<ExistingTransactionTag> tags = [];
    private List<string> currencyCodes = [];

    /// <summary>Selectable contacts, including any created inline during this review.</summary>
    public IReadOnlyList<ExistingContact> Contacts => contacts;

    /// <summary>Options for the merchant combobox.</summary>
    public IReadOnlyList<OdsOption> ContactOptions { get; private set; } = [];

    /// <summary>Options for the category tag picker.</summary>
    public IReadOnlyList<OdsOption> TagOptions { get; private set; } = [];

    /// <summary>Contacts created inline during this review, so their indicator reads "Created here".</summary>
    private readonly HashSet<Guid> createdContactIds = [];

    /// <summary>The number of contact + tag names that would be sent for matching (consent + progress copy).</summary>
    public int VocabularyCount => contacts.Count + tags.Count;

    /// <summary>
    /// The effective auto-link policy is owned by the server and echoed on the job DTO — never a client
    /// literal that could silently drift from the persisted MatchMethod. ≥ threshold ⇒ the cell is
    /// auto-filled; below ⇒ a suggestion chip the reviewer can Apply. (Falls back to the documented
    /// server defaults only before a job is loaded.)
    /// </summary>
    public decimal AutoLinkThreshold => Job is { } job ? (decimal)job.AutoLinkThreshold : 0.60m;

    /// <summary>The server's cap on how many names may be sent for matching.</summary>
    public int MaxVocabulary => Job?.MaxVocabulary ?? 500;

    public void SetContacts(IEnumerable<ExistingContact> loaded)
    {
        contacts.Clear();
        contacts.AddRange(loaded.Where(c => c.Archived is null).OrderBy(c => c.ResolvedDisplayName));
        ContactOptions = [.. contacts.Select(c => ContactOption(c.ContactId, c.ResolvedDisplayName))];
    }

    public void SetTags(IEnumerable<ExistingTransactionTag> loaded)
    {
        tags.Clear();
        tags.AddRange(loaded.Where(t => t.Archived is null).OrderBy(t => t.Name));
        TagOptions = [.. tags.Select(t => new OdsOption(t.TransactionTagId.ToString(), t.Name))];
    }

    public void SetCurrencies(IEnumerable<string> codes) => currencyCodes = [.. codes];

    /// <summary>The currency list for a row, keeping an unknown code the statement carried.</summary>
    public IEnumerable<string> CurrencyOptions(string current) =>
        !string.IsNullOrWhiteSpace(current) && !currencyCodes.Contains(current, StringComparer.OrdinalIgnoreCase)
            ? currencyCodes.Prepend(current)
            : currencyCodes;

    /// <summary>The same codes shaped for <c>OdsCurrencySelect</c>. Code-only options: the reference
    /// list this session carries is codes, and the grid's currency column shows no name anyway.</summary>
    public IReadOnlyList<OdsOption> CurrencyPickerOptions(string current) =>
        [.. CurrencyOptions(current).Select(OdsOption.From)];

    private static OdsOption ContactOption(Guid id, string name) =>
        new(id.ToString(), name) { Icon = "storefront" };

    private string TagName(Guid id) => tags.FirstOrDefault(t => t.TransactionTagId == id)?.Name ?? id.ToString();

    // ── Review rollups ────────────────────────────────────────────────────────
    public int SelectedCount => Rows.Count(r => r.Selected);
    public bool AllSelected => Rows.Count > 0 && Rows.All(r => r.Selected);

    /// <summary>Mixed state for the "select all" header — some but not all rows selected.</summary>
    public bool SomeSelected => Rows.Count > 0 && Rows.Any(r => r.Selected) && !Rows.All(r => r.Selected);

    public decimal Net => Rows.Where(r => r.Selected).Sum(r => r.Amount);

    // ── Phase ─────────────────────────────────────────────────────────────────
    public void SetPhase(FileAnalysisPhase phase)
    {
        Phase = phase;
        ActionAnnounce = null; // a stale action message must not override the new phase's announcement
        (Title, Subtitle) = PhaseText(phase);
    }

    /// <summary>Records a transient announcement for the dialog's polite live region.</summary>
    public void Announce(string message) => ActionAnnounce = message;

    private static (string Title, string Subtitle) PhaseText(FileAnalysisPhase phase) => phase switch
    {
        FileAnalysisPhase.Consent => ("Send this statement to Claude?", "Analysis is done by an external AI provider. Review what leaves Odyssey, then confirm."),
        FileAnalysisPhase.Analyzing => ("Analyzing statement", "Reading the document and extracting candidate transactions."),
        FileAnalysisPhase.Matching => ("Matching merchants and categories", "Comparing each candidate against your contacts and tags."),
        FileAnalysisPhase.Blocked => ("This file can’t be analyzed", "Analysis extracts transactions from bank statements."),
        FileAnalysisPhase.Disabled => ("Analysis isn’t available", "The feature is not enabled on this server."),
        FileAnalysisPhase.Failed => ("Analysis failed", "The statement couldn’t be processed."),
        FileAnalysisPhase.Empty => ("No transactions found", "The analysis completed, but nothing looked like a transaction."),
        FileAnalysisPhase.Review => ("Review candidate transactions", "Edit any row, untick what you don’t want, then import the rest."),
        FileAnalysisPhase.ReanalyzeConfirm => ("You’ve already analyzed this file", "Pick up the review you left — or send the statement to Claude again."),
        FileAnalysisPhase.ResumeLoading => ("Opening your review", "Loading the candidates you saved — no new analysis."),
        FileAnalysisPhase.NoLongerAvailable => ("This review is no longer available", "The saved analysis can’t be opened."),
        _ => ("Analyze statement", string.Empty),
    };

    /// <summary>Switches to the Done step, whose title/subtitle name the account that received the import.</summary>
    public void SetImported(string? accountName)
    {
        Phase = FileAnalysisPhase.Done;
        ActionAnnounce = null;
        Title = "Import complete";
        Subtitle = $"Candidates committed to {(string.IsNullOrWhiteSpace(accountName) ? "your account" : accountName)} as New transactions.";
    }

    // ── Seeding rows from a job ───────────────────────────────────────────────
    public void SeedRows()
    {
        Rows.Clear();
        if (Job is null)
            return;

        var matched = MatchStatus == FileAnalysisMatchStatus.Completed;

        // Only pending candidates are reviewable — on a resumed, partially-triaged job the already
        // imported (Accepted/Rejected) candidates are excluded so they can't be re-imported. A fresh
        // analysis has only pending candidates, so this is a no-op there.
        foreach (var c in Job.Candidates.Where(c => c.ReviewStatus == CandidateTransactionReviewStatus.Pending))
        {
            var row = new FileAnalysisRow
            {
                CandidateId = c.Id,
                Selected = c.LlmConfidence is null || c.LlmConfidence >= 0.6m, // low EXTRACTION confidence starts unchecked
                TransactionDate = c.TransactionDate,
                Description = c.Description,
                Merchant = c.Merchant ?? string.Empty,
                CategoryHint = c.CategoryHint,
                Amount = c.Amount,
                Currency = c.Currency,
                Reference = c.ReferenceNumber ?? c.ExternalId ?? string.Empty,
                LlmConfidence = c.LlmConfidence,
            };

            ApplyCandidateMatch(row, c, matched);
            Rows.Add(row);
        }
    }

    /// <summary>
    /// Applies a candidate's persisted match data to a fresh row. A match ≥ <see cref="AutoLinkThreshold"/>
    /// auto-links (source = AI); a sub-threshold match is kept as a suggestion chip (not pre-filled); no
    /// match / not matched leaves the cell empty (None) so the reviewer links by hand.
    /// </summary>
    private void ApplyCandidateMatch(FileAnalysisRow row, ExistingFileAnalysisCandidateTransaction c, bool matched)
    {
        if (matched && c.MatchedContactId is { } contactId)
        {
            var conf = c.MerchantMatchConfidence;
            if (conf is not null && conf >= AutoLinkThreshold)
            {
                row.ContactId = contactId;
                row.Merchant = c.MatchedContactName ?? row.Merchant;
                row.MerchantSource = OdsMatchState.Ai;
                row.MerchantConf = conf;
            }
            else
            {
                row.MerchantSuggestion = new FileAnalysisMatchSuggestion(c.MatchedContactName ?? "a contact", conf);
                row.MerchantSuggestionId = contactId;
            }
        }

        if (matched && c.MatchedTagIds.Count > 0)
        {
            var conf = c.CategoryMatchConfidence;
            if (conf is not null && conf >= AutoLinkThreshold)
            {
                row.TagIds = [.. c.MatchedTagIds.Select(id => id.ToString())];
                row.CatSource = OdsMatchState.Ai;
                row.CatConf = conf;
            }
            else
            {
                row.CatSuggestion = new FileAnalysisMatchSuggestion(string.Join(", ", c.MatchedTagIds.Select(TagName)), conf);
                row.CatSuggestionIds = [.. c.MatchedTagIds.Select(id => id.ToString())];
            }
        }
    }

    /// <summary>
    /// Re-run idempotency (client side): refresh None/AI suggestions from the freshly-matched job, but
    /// keep any row the reviewer set to Manual / Created (a re-run never clobbers a human decision).
    /// Mirrors the server's transactional "preserve Manual rows" idempotency.
    /// </summary>
    public void ApplyMatchesPreservingManual()
    {
        if (Job is null)
            return;

        var matched = MatchStatus == FileAnalysisMatchStatus.Completed;
        var byId = Job.Candidates.ToDictionary(c => c.Id);

        foreach (var row in Rows)
        {
            if (!byId.TryGetValue(row.CandidateId, out var c))
                continue;

            if (row.MerchantSource is not (OdsMatchState.Manual or OdsMatchState.Created))
            {
                row.ContactId = null;
                row.MerchantSource = OdsMatchState.None;
                row.MerchantConf = null;
                row.MerchantSuggestion = null;
                row.MerchantSuggestionId = null;
                ApplyCandidateMerchant(row, c, matched);
            }

            if (row.CatSource != OdsMatchState.Manual)
            {
                row.TagIds = [];
                row.CatSource = OdsMatchState.None;
                row.CatConf = null;
                row.CatSuggestion = null;
                row.CatSuggestionIds = [];
                ApplyCandidateCategory(row, c, matched);
            }
        }
    }

    private void ApplyCandidateMerchant(FileAnalysisRow row, ExistingFileAnalysisCandidateTransaction c, bool matched)
    {
        if (!matched || c.MatchedContactId is not { } contactId)
            return;

        var conf = c.MerchantMatchConfidence;
        if (conf is not null && conf >= AutoLinkThreshold)
        {
            row.ContactId = contactId;
            row.Merchant = c.MatchedContactName ?? row.Merchant;
            row.MerchantSource = OdsMatchState.Ai;
            row.MerchantConf = conf;
        }
        else
        {
            row.MerchantSuggestion = new FileAnalysisMatchSuggestion(c.MatchedContactName ?? "a contact", conf);
            row.MerchantSuggestionId = contactId;
        }
    }

    private void ApplyCandidateCategory(FileAnalysisRow row, ExistingFileAnalysisCandidateTransaction c, bool matched)
    {
        if (!matched || c.MatchedTagIds.Count == 0)
            return;

        var conf = c.CategoryMatchConfidence;
        if (conf is not null && conf >= AutoLinkThreshold)
        {
            row.TagIds = [.. c.MatchedTagIds.Select(id => id.ToString())];
            row.CatSource = OdsMatchState.Ai;
            row.CatConf = conf;
        }
        else
        {
            row.CatSuggestion = new FileAnalysisMatchSuggestion(string.Join(", ", c.MatchedTagIds.Select(TagName)), conf);
            row.CatSuggestionIds = [.. c.MatchedTagIds.Select(id => id.ToString())];
        }
    }

    // ── Row editing ───────────────────────────────────────────────────────────
    public void ToggleRow(FileAnalysisRow row) => row.Selected = !row.Selected;

    public void ToggleAll()
    {
        var target = !AllSelected;
        foreach (var row in Rows)
            row.Selected = target;
    }

    public void RemoveRow(FileAnalysisRow row) => Rows.Remove(row);

    public static void SetDate(FileAnalysisRow row, string? value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            row.TransactionDate = parsed;
    }

    public static void SetAmount(FileAnalysisRow row, string? value)
    {
        row.AmountText = value ?? string.Empty;
        var normalized = (value ?? string.Empty).Replace(",", string.Empty);
        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            row.Amount = parsed;
    }

    /// <summary>
    /// The amount cell's keystroke rules, the same ones <c>OdsMoneyField</c> applies in a labelled
    /// form: characters that are never part of an amount are dropped, and a second decimal separator
    /// or a minus that isn't leading is REJECTED (<c>null</c>) rather than rewritten — the caller puts
    /// the cell back to what it held. The grid keeps a bare input here because the column is too
    /// narrow for a labelled control, not because the rules differ.
    /// </summary>
    public static string? SanitizeAmount(string? raw)
    {
        var kept = new string([.. (raw ?? string.Empty)
            .Where(ch => char.IsAsciiDigit(ch) || ch is '.' or ',' or '-' || char.IsWhiteSpace(ch))]);

        if (kept.Count(ch => ch is '.' or ',') > 1) return null;
        if (kept.IndexOf('-') > 0) return null;
        return kept;
    }

    /// <summary>Sets the amount's sign without touching its magnitude — typing − / + in the cell
    /// picks the direction rather than inserting a character.</summary>
    public static void SetAmountSign(FileAnalysisRow row, bool negative)
    {
        var magnitude = row.AmountText.TrimStart().TrimStart('-');
        SetAmount(row, (negative ? "-" : string.Empty) + magnitude);
    }

    // ── Match state (merchant → contact) ──────────────────────────────────────

    /// <summary>
    /// The visible match state: an auto-linked / chosen / created cell shows its source; an unlinked
    /// cell with a sub-threshold suggestion shows the suggestion chip; otherwise "No match".
    /// </summary>
    public static OdsMatchState MerchantState(FileAnalysisRow row) =>
        row.ContactId is not null
            ? row.MerchantSource
            : row.MerchantSuggestion is not null ? OdsMatchState.Suggestion : OdsMatchState.None;

    public static OdsMatchState CategoryState(FileAnalysisRow row) =>
        row.TagIds.Count > 0
            ? row.CatSource
            : row.CatSuggestion is not null ? OdsMatchState.Suggestion : OdsMatchState.None;

    /// <summary>
    /// A reviewer pick resolves its provenance from the created-here set: an inline-created contact
    /// reads "Created here", any existing one "You chose".
    /// </summary>
    public void SelectContact(FileAnalysisRow row, string? value)
    {
        if (!Guid.TryParse(value, out var id))
        {
            row.ContactId = null;
            row.MerchantSource = OdsMatchState.None;
            row.MerchantConf = null;
            row.MerchantSuggestion = null;
            return;
        }

        row.ContactId = id;
        row.Merchant = contacts.FirstOrDefault(c => c.ContactId == id)?.ResolvedDisplayName ?? row.Merchant;
        row.MerchantSource = createdContactIds.Contains(id) ? OdsMatchState.Created : OdsMatchState.Manual;
        row.MerchantConf = null;
        row.MerchantSuggestion = null;
    }

    /// <summary>The longest name the quick-create accepts; the server clamps to the same limit.</summary>
    public const int MaxContactNameLength = 128;

    /// <summary>
    /// Stages an optimistic inline create: a temporary id is linked to the row and added to the
    /// option list so the name is selectable everywhere at once. The caller POSTs the contact and
    /// then calls <see cref="ReconcileCreatedContact"/> or <see cref="RollbackCreatedContact"/>.
    /// Returns the staged option, or null when the typed text is blank.
    /// </summary>
    public OdsOption? BeginCreateContact(FileAnalysisRow row, string? text, out Guid tempId)
    {
        tempId = Guid.Empty;
        var name = (text ?? string.Empty).Trim();
        if (name.Length == 0)
            return null;
        if (name.Length > MaxContactNameLength)
            name = name[..MaxContactNameLength];

        tempId = Guid.NewGuid();
        createdContactIds.Add(tempId);
        var option = ContactOption(tempId, name);
        ContactOptions = [.. ContactOptions, option];
        contacts.Add(new ExistingContact
        {
            ContactId = tempId,
            ExternalUid = $"urn:uuid:{tempId}",
            ResolvedDisplayName = name,
            NormalizedName = name.ToUpperInvariant(),
            Type = ContactType.Organization,
        });

        row.ContactId = tempId;
        row.Merchant = name;
        row.MerchantSource = OdsMatchState.Created;
        row.MerchantConf = null;
        row.MerchantSuggestion = null;
        return option;
    }

    /// <summary>Swaps the optimistic temp id for the server's real id everywhere it landed.</summary>
    public void ReconcileCreatedContact(Guid tempId, Guid realId, string name)
    {
        createdContactIds.Remove(tempId);
        createdContactIds.Add(realId);

        var contact = contacts.FirstOrDefault(c => c.ContactId == tempId);
        if (contact is not null)
            contact.ContactId = realId;

        ContactOptions = [.. ContactOptions.Select(o => o.Value == tempId.ToString() ? ContactOption(realId, name) : o)];

        foreach (var row in Rows.Where(r => r.ContactId == tempId))
            row.ContactId = realId;
    }

    /// <summary>
    /// Undoes a staged create the server rejected: the temp contact and its option disappear and every
    /// row that had been optimistically linked to it goes back to "No match". Without this the row
    /// would keep pointing at an id no server ever issued, and the import would fail on it.
    /// </summary>
    public void RollbackCreatedContact(Guid tempId)
    {
        createdContactIds.Remove(tempId);
        contacts.RemoveAll(c => c.ContactId == tempId);
        ContactOptions = [.. ContactOptions.Where(o => o.Value != tempId.ToString())];
        foreach (var row in Rows.Where(r => r.ContactId == tempId))
        {
            row.ContactId = null;
            row.MerchantSource = OdsMatchState.None;
        }
    }

    /// <summary>
    /// Whether to offer the one-click "Create ‹merchant›": only when the reviewer can create contacts,
    /// the cell is unlinked with no pending suggestion, there is an extracted merchant string, and no
    /// existing contact already has that exact name (so we never offer to create a duplicate).
    /// </summary>
    public bool CanQuickCreateMerchant(FileAnalysisRow row) =>
        CanCreateContact
        && row.ContactId is null
        && row.MerchantSuggestion is null
        && !string.IsNullOrWhiteSpace(row.Merchant)
        && !contacts.Any(c => string.Equals(c.ResolvedDisplayName, row.Merchant.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Applying a sub-threshold suggestion links the matched contact and marks the row a reviewer
    /// decision (Manual). Returns the linked name for the caller's announcement, or null if there was
    /// nothing to apply.
    /// </summary>
    public string? ApplyMerchantSuggestion(FileAnalysisRow row)
    {
        if (row.MerchantSuggestion is null || row.MerchantSuggestionId is not { } contactId)
            return null;

        var name = row.MerchantSuggestion.Name;
        row.ContactId = contactId;
        row.Merchant = name;
        row.MerchantSource = OdsMatchState.Manual;
        row.MerchantConf = null;
        row.MerchantSuggestion = null;
        return name;
    }

    public static void DismissMerchantSuggestion(FileAnalysisRow row) => row.MerchantSuggestion = null;

    // ── Match state (category → tags) ─────────────────────────────────────────
    public static void SetCategory(FileAnalysisRow row, IEnumerable<string> ids)
    {
        row.TagIds = [.. ids];
        row.CatSource = OdsMatchState.Manual;
        row.CatConf = null;
        row.CatSuggestion = null;
    }

    public static bool ApplyCategorySuggestion(FileAnalysisRow row)
    {
        if (row.CatSuggestion is null)
            return false;

        row.TagIds = [.. row.CatSuggestionIds];
        row.CatSource = OdsMatchState.Manual;
        row.CatConf = null;
        row.CatSuggestion = null;
        return true;
    }

    public static void DismissCategorySuggestion(FileAnalysisRow row) => row.CatSuggestion = null;

    // ── Import ────────────────────────────────────────────────────────────────

    /// <summary>The selected rows, as the import endpoint's request body.</summary>
    public ImportRequest BuildImportRequest() => new(
    [
        .. Rows.Where(r => r.Selected).Select(r => new ImportCandidateRequest(
            r.CandidateId,
            r.TransactionDate,
            r.Description,
            r.Amount,
            r.Currency,
            r.ContactId,
            [.. r.TagIds.Select(Guid.Parse)],
            string.IsNullOrWhiteSpace(r.Reference) ? null : r.Reference.Trim())),
    ]);

    // ── Formatting ────────────────────────────────────────────────────────────
    public static string FormatAmount(decimal amount) => amount.ToString("0.##", CultureInfo.InvariantCulture);

    public static string FormatSigned(decimal value) =>
        (value < 0 ? "−" : "+") + Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>The extraction-confidence meter's fill percentage and tone; null percent renders an em dash.</summary>
    public static (int? Pct, string Tone) ConfidenceBand(decimal? confidence)
    {
        if (confidence is null)
            return (null, "empty");
        var pct = (int)Math.Round(confidence.Value * 100);
        var tone = confidence >= 0.85m ? "info" : confidence >= 0.60m ? "pending" : "expense";
        return (pct, tone);
    }
}
