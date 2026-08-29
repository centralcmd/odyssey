using Odyssey.Client.Components;
using Odyssey.Client.Models;
using Odyssey.Dtos.Finance;
using Odyssey.Dtos.Journal;
using Odyssey.Dtos;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// Unit tests for <see cref="FileAnalysisSession"/> — the phase/rows/matching/create-rollback state
/// that issue #373 pulled out of <c>FileAnalysisDialog</c>'s 960-line <c>@code</c> block so it could
/// be tested at all. The rollback path is the reason: a create the server rejects must leave no row
/// pointing at an id that was never issued, and nothing in the dialog could reach that branch.
/// </summary>
public class FileAnalysisSessionTests
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Fixtures
    // ─────────────────────────────────────────────────────────────────────────

    private static ExistingContact Contact(Guid id, string name, DateTime? archived = null) => new()
    {
        ContactId = id,
        ExternalUid = $"urn:uuid:{id}",
        ResolvedDisplayName = name,
        NormalizedName = name.ToUpperInvariant(),
        Type = ContactType.Organization,
        Archived = archived,
    };

    private static ExistingFileAnalysisCandidateTransaction Candidate(
        Guid? id = null,
        decimal amount = -100m,
        string description = "CARD PURCHASE",
        string? merchant = "REMA 1000",
        decimal? llmConfidence = 0.9m,
        CandidateTransactionReviewStatus review = CandidateTransactionReviewStatus.Pending,
        Guid? matchedContactId = null,
        string? matchedContactName = null,
        decimal? merchantConfidence = null,
        List<Guid>? matchedTagIds = null,
        decimal? categoryConfidence = null) =>
        new(
            id ?? Guid.NewGuid(),
            new DateTime(2026, 3, 4),
            null,
            description,
            merchant,
            "Groceries",
            amount,
            "NOK",
            null,
            "REF-1",
            llmConfidence,
            "claude",
            review,
            null,
            matchedContactId,
            matchedContactName,
            matchedTagIds ?? [],
            merchantConfidence,
            categoryConfidence,
            MatchMethod.None);

    private static ExistingFileAnalysisJob Job(
        params ExistingFileAnalysisCandidateTransaction[] candidates) =>
        JobWith(FileAnalysisMatchStatus.Completed, 0.60, candidates);

    private static ExistingFileAnalysisJob JobWith(
        FileAnalysisMatchStatus matchStatus,
        double autoLinkThreshold,
        params ExistingFileAnalysisCandidateTransaction[] candidates) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            FileAnalysisJobStatus.Completed,
            "statement",
            null, null, null, null,
            "Anthropic",
            "claude-opus-4-7",
            "v1",
            [.. candidates],
            matchStatus,
            null,
            autoLinkThreshold,
            500);

    /// <summary>A session with one pending candidate seeded and no vocabulary.</summary>
    private static FileAnalysisSession SeededSession(out FileAnalysisRow row)
    {
        var session = new FileAnalysisSession { CanCreateContact = true };
        session.Job = Job(Candidate());
        session.MatchStatus = FileAnalysisMatchStatus.Completed;
        session.SeedRows();
        row = session.Rows.Single();
        return session;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Inline merchant create → rollback
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Begin_create_contact_stages_the_option_the_contact_and_the_row_link()
    {
        var session = SeededSession(out var row);

        var option = session.BeginCreateContact(row, "  Kiwi Minipris  ", out var tempId);

        Assert.NotNull(option);
        Assert.NotEqual(Guid.Empty, tempId);
        Assert.Equal("Kiwi Minipris", option!.Label); // trimmed
        Assert.Equal(tempId.ToString(), option.Value);

        Assert.Contains(session.ContactOptions, o => o.Value == tempId.ToString());
        Assert.Contains(session.Contacts, c => c.ContactId == tempId);

        Assert.Equal(tempId, row.ContactId);
        Assert.Equal("Kiwi Minipris", row.Merchant);
        Assert.Equal(OdsMatchState.Created, row.MerchantSource);
    }

    /// <summary>
    /// The failure branch: the POST came back an error, so the staged contact must disappear
    /// completely. A row left linked to the temp id would be sent to the import endpoint and rejected.
    /// </summary>
    [Fact]
    public void Rollback_removes_the_staged_contact_its_option_and_every_row_link()
    {
        var session = new FileAnalysisSession { CanCreateContact = true };
        session.Job = Job(Candidate(merchant: "REMA 1000"), Candidate(merchant: "REMA 1000"));
        session.MatchStatus = FileAnalysisMatchStatus.Completed;
        session.SeedRows();
        var (first, second) = (session.Rows[0], session.Rows[1]);

        session.BeginCreateContact(first, "Kiwi", out var tempId);
        // The same staged contact is then picked on a second row.
        session.SelectContact(second, tempId.ToString());
        Assert.Equal(tempId, second.ContactId);

        session.RollbackCreatedContact(tempId);

        Assert.DoesNotContain(session.Contacts, c => c.ContactId == tempId);
        Assert.DoesNotContain(session.ContactOptions, o => o.Value == tempId.ToString());
        foreach (var row in new[] { first, second })
        {
            Assert.Null(row.ContactId);
            Assert.Equal(OdsMatchState.None, row.MerchantSource);
        }
    }

    /// <summary>
    /// Rollback must not disturb rows linked to other contacts — including one staged by a *different*
    /// inline create that did succeed.
    /// </summary>
    [Fact]
    public void Rollback_leaves_rows_linked_to_other_contacts_alone()
    {
        var session = new FileAnalysisSession { CanCreateContact = true };
        session.Job = Job(Candidate(), Candidate());
        session.MatchStatus = FileAnalysisMatchStatus.Completed;
        session.SeedRows();
        var (doomed, survivor) = (session.Rows[0], session.Rows[1]);

        session.BeginCreateContact(doomed, "Doomed", out var doomedId);
        session.BeginCreateContact(survivor, "Survivor", out var survivorId);

        session.RollbackCreatedContact(doomedId);

        Assert.Null(doomed.ContactId);
        Assert.Equal(survivorId, survivor.ContactId);
        Assert.Equal(OdsMatchState.Created, survivor.MerchantSource);
        Assert.Contains(session.ContactOptions, o => o.Value == survivorId.ToString());
    }

    /// <summary>After a rollback the quick-create affordance comes back, so the reviewer can retry.</summary>
    [Fact]
    public void Rollback_restores_the_quick_create_affordance_for_the_extracted_merchant()
    {
        var session = SeededSession(out var row);
        Assert.True(session.CanQuickCreateMerchant(row));

        session.BeginCreateContact(row, row.Merchant, out var tempId);
        Assert.False(session.CanQuickCreateMerchant(row)); // already linked

        session.RollbackCreatedContact(tempId);

        Assert.True(session.CanQuickCreateMerchant(row));
    }

    [Fact]
    public void Reconcile_swaps_the_temp_id_for_the_server_id_everywhere_it_landed()
    {
        var session = SeededSession(out var row);
        session.BeginCreateContact(row, "Kiwi", out var tempId);
        var realId = Guid.NewGuid();

        session.ReconcileCreatedContact(tempId, realId, "Kiwi");

        Assert.Equal(realId, row.ContactId);
        Assert.DoesNotContain(session.ContactOptions, o => o.Value == tempId.ToString());
        Assert.Contains(session.ContactOptions, o => o.Value == realId.ToString() && o.Label == "Kiwi");
        Assert.Contains(session.Contacts, c => c.ContactId == realId);
        Assert.DoesNotContain(session.Contacts, c => c.ContactId == tempId);
    }

    /// <summary>
    /// The reconciled id stays in the created-here set, so re-picking it still reads "Created here"
    /// rather than the weaker "You chose".
    /// </summary>
    [Fact]
    public void A_reconciled_contact_is_still_attributed_as_created_here()
    {
        var session = SeededSession(out var row);
        session.BeginCreateContact(row, "Kiwi", out var tempId);
        var realId = Guid.NewGuid();
        session.ReconcileCreatedContact(tempId, realId, "Kiwi");

        session.SelectContact(row, realId.ToString());

        Assert.Equal(OdsMatchState.Created, row.MerchantSource);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Begin_create_contact_refuses_blank_names(string? text)
    {
        var session = SeededSession(out var row);

        var option = session.BeginCreateContact(row, text, out var tempId);

        Assert.Null(option);
        Assert.Equal(Guid.Empty, tempId);
        Assert.Empty(session.Contacts);
        Assert.Null(row.ContactId);
    }

    [Fact]
    public void Begin_create_contact_clamps_the_name_to_the_servers_limit()
    {
        var session = SeededSession(out var row);

        var option = session.BeginCreateContact(row, new string('x', 400), out _);

        Assert.Equal(FileAnalysisSession.MaxContactNameLength, option!.Label.Length);
        Assert.Equal(option.Label, row.Merchant);
    }

    [Fact]
    public void Quick_create_is_hidden_without_the_contacts_create_claim()
    {
        var session = SeededSession(out var row);
        session.CanCreateContact = false;

        Assert.False(session.CanQuickCreateMerchant(row));
    }

    [Fact]
    public void Quick_create_is_hidden_when_a_contact_with_that_name_already_exists()
    {
        var session = SeededSession(out var row);
        session.SetContacts([Contact(Guid.NewGuid(), "rema 1000")]); // case-insensitive

        Assert.False(session.CanQuickCreateMerchant(row));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Seeding + match application
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Seed_rows_takes_only_pending_candidates()
    {
        var session = new FileAnalysisSession
        {
            Job = Job(
                Candidate(review: CandidateTransactionReviewStatus.Pending, description: "keep"),
                Candidate(review: CandidateTransactionReviewStatus.Accepted, description: "drop"),
                Candidate(review: CandidateTransactionReviewStatus.Rejected, description: "drop")),
        };

        session.SeedRows();

        Assert.Equal("keep", Assert.Single(session.Rows).Description);
    }

    [Fact]
    public void Low_extraction_confidence_starts_unticked()
    {
        var session = new FileAnalysisSession
        {
            Job = Job(Candidate(llmConfidence: 0.4m), Candidate(llmConfidence: 0.6m), Candidate(llmConfidence: null)),
        };

        session.SeedRows();

        Assert.Equal([false, true, true], session.Rows.Select(r => r.Selected));
    }

    [Fact]
    public void A_match_at_or_above_the_threshold_auto_links_the_merchant()
    {
        var contactId = Guid.NewGuid();
        var session = new FileAnalysisSession
        {
            MatchStatus = FileAnalysisMatchStatus.Completed,
            Job = Job(Candidate(matchedContactId: contactId, matchedContactName: "Rema 1000", merchantConfidence: 0.72m)),
        };

        session.SeedRows();
        var row = session.Rows.Single();

        Assert.Equal(contactId, row.ContactId);
        Assert.Equal("Rema 1000", row.Merchant);
        Assert.Equal(OdsMatchState.Ai, row.MerchantSource);
        Assert.Null(row.MerchantSuggestion);
    }

    [Fact]
    public void A_sub_threshold_match_becomes_a_suggestion_rather_than_a_link()
    {
        var contactId = Guid.NewGuid();
        var session = new FileAnalysisSession
        {
            MatchStatus = FileAnalysisMatchStatus.Completed,
            Job = Job(Candidate(matchedContactId: contactId, matchedContactName: "Rema 1000", merchantConfidence: 0.4m)),
        };

        session.SeedRows();
        var row = session.Rows.Single();

        Assert.Null(row.ContactId);
        Assert.Equal(OdsMatchState.Suggestion, FileAnalysisSession.MerchantState(row));
        Assert.Equal("Rema 1000", row.MerchantSuggestion!.Name);
        Assert.Equal(contactId, row.MerchantSuggestionId);
    }

    [Fact]
    public void The_auto_link_threshold_comes_from_the_job_not_a_client_literal()
    {
        var contactId = Guid.NewGuid();
        var session = new FileAnalysisSession
        {
            MatchStatus = FileAnalysisMatchStatus.Completed,
            // A server that auto-links at 0.30 must auto-link a 0.40 match the default would suggest.
            Job = JobWith(FileAnalysisMatchStatus.Completed, 0.30,
                Candidate(matchedContactId: contactId, matchedContactName: "Rema 1000", merchantConfidence: 0.4m)),
        };

        session.SeedRows();

        Assert.Equal(contactId, session.Rows.Single().ContactId);
    }

    [Fact]
    public void Matches_are_ignored_entirely_when_the_match_step_did_not_complete()
    {
        var session = new FileAnalysisSession
        {
            MatchStatus = FileAnalysisMatchStatus.Failed,
            Job = Job(Candidate(matchedContactId: Guid.NewGuid(), matchedContactName: "Rema 1000", merchantConfidence: 0.99m)),
        };

        session.SeedRows();
        var row = session.Rows.Single();

        Assert.Null(row.ContactId);
        Assert.Equal(OdsMatchState.None, FileAnalysisSession.MerchantState(row));
    }

    [Fact]
    public void Applying_a_suggestion_links_it_and_records_it_as_a_reviewer_decision()
    {
        var contactId = Guid.NewGuid();
        var session = new FileAnalysisSession
        {
            MatchStatus = FileAnalysisMatchStatus.Completed,
            Job = Job(Candidate(matchedContactId: contactId, matchedContactName: "Rema 1000", merchantConfidence: 0.4m)),
        };
        session.SeedRows();
        var row = session.Rows.Single();

        var name = session.ApplyMerchantSuggestion(row);

        Assert.Equal("Rema 1000", name);
        Assert.Equal(contactId, row.ContactId);
        Assert.Equal(OdsMatchState.Manual, row.MerchantSource);
        Assert.Null(row.MerchantSuggestion);
    }

    /// <summary>A re-match refreshes AI/None rows but never clobbers a human decision.</summary>
    [Fact]
    public void Re_matching_preserves_manual_and_created_rows()
    {
        var candidateA = Candidate();
        var candidateB = Candidate();
        var candidateC = Candidate();
        var session = new FileAnalysisSession
        {
            CanCreateContact = true,
            MatchStatus = FileAnalysisMatchStatus.Completed,
            Job = Job(candidateA, candidateB, candidateC),
        };
        session.SeedRows();
        var (manual, created, untouched) = (session.Rows[0], session.Rows[1], session.Rows[2]);

        var chosenId = Guid.NewGuid();
        session.SetContacts([Contact(chosenId, "Chosen By Hand")]);
        session.SelectContact(manual, chosenId.ToString());
        session.BeginCreateContact(created, "Created Here", out var createdId);

        // The server re-matched and now proposes a different contact for every row.
        var proposed = Guid.NewGuid();
        session.Job = Job(
            candidateA with { MatchedContactId = proposed, MatchedContactName = "Proposed", MerchantMatchConfidence = 0.95m },
            candidateB with { MatchedContactId = proposed, MatchedContactName = "Proposed", MerchantMatchConfidence = 0.95m },
            candidateC with { MatchedContactId = proposed, MatchedContactName = "Proposed", MerchantMatchConfidence = 0.95m });

        session.ApplyMatchesPreservingManual();

        Assert.Equal(chosenId, manual.ContactId);
        Assert.Equal(OdsMatchState.Manual, manual.MerchantSource);
        Assert.Equal(createdId, created.ContactId);
        Assert.Equal(OdsMatchState.Created, created.MerchantSource);
        Assert.Equal(proposed, untouched.ContactId);
        Assert.Equal(OdsMatchState.Ai, untouched.MerchantSource);
    }

    [Fact]
    public void Re_matching_clears_a_stale_link_when_the_new_match_is_gone()
    {
        var candidate = Candidate(matchedContactId: Guid.NewGuid(), matchedContactName: "Old", merchantConfidence: 0.95m);
        var session = new FileAnalysisSession
        {
            MatchStatus = FileAnalysisMatchStatus.Completed,
            Job = Job(candidate),
        };
        session.SeedRows();
        var row = session.Rows.Single();
        Assert.Equal(OdsMatchState.Ai, row.MerchantSource);

        session.Job = Job(candidate with { MatchedContactId = null, MatchedContactName = null, MerchantMatchConfidence = null });
        session.ApplyMatchesPreservingManual();

        Assert.Null(row.ContactId);
        Assert.Equal(OdsMatchState.None, row.MerchantSource);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Category cells
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_sub_threshold_tag_match_names_the_tags_it_would_apply()
    {
        var tagId = Guid.NewGuid();
        var session = new FileAnalysisSession { MatchStatus = FileAnalysisMatchStatus.Completed };
        session.SetTags([new ExistingTransactionTag { TransactionTagId = tagId, Name = "Groceries", Archived = null }]);
        session.Job = Job(Candidate(matchedTagIds: [tagId], categoryConfidence: 0.2m));

        session.SeedRows();
        var row = session.Rows.Single();

        Assert.Empty(row.TagIds);
        Assert.Equal("Groceries", row.CatSuggestion!.Name);
        Assert.Equal([tagId.ToString()], row.CatSuggestionIds);

        Assert.True(FileAnalysisSession.ApplyCategorySuggestion(row));
        Assert.Equal([tagId.ToString()], row.TagIds);
        Assert.Equal(OdsMatchState.Manual, row.CatSource);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Vocabulary
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Archived_contacts_and_tags_are_not_offered_and_are_not_counted()
    {
        var session = new FileAnalysisSession();
        session.SetContacts([Contact(Guid.NewGuid(), "Live"), Contact(Guid.NewGuid(), "Gone", DateTime.UtcNow)]);
        session.SetTags(
        [
            new ExistingTransactionTag { TransactionTagId = Guid.NewGuid(), Name = "Live", Archived = null },
            new ExistingTransactionTag { TransactionTagId = Guid.NewGuid(), Name = "Gone", Archived = DateTime.UtcNow },
        ]);

        Assert.Equal("Live", Assert.Single(session.ContactOptions).Label);
        Assert.Equal("Live", Assert.Single(session.TagOptions).Label);
        Assert.Equal(2, session.VocabularyCount);
    }

    [Fact]
    public void The_currency_list_keeps_a_code_the_statement_carried_but_the_server_does_not_know()
    {
        var session = new FileAnalysisSession();
        session.SetCurrencies(["NOK", "EUR"]);

        Assert.Equal(["ZWL", "NOK", "EUR"], session.CurrencyOptions("ZWL"));
        Assert.Equal(["NOK", "EUR"], session.CurrencyOptions("NOK"));
        Assert.Equal(["NOK", "EUR"], session.CurrencyOptions(""));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Row editing, rollups and the import body
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Toggle_all_selects_everything_then_clears_everything()
    {
        var session = new FileAnalysisSession { Job = Job(Candidate(llmConfidence: 0.4m), Candidate()) };
        session.SeedRows();
        Assert.True(session.SomeSelected);
        Assert.False(session.AllSelected);

        session.ToggleAll();
        Assert.True(session.AllSelected);
        Assert.False(session.SomeSelected);

        session.ToggleAll();
        Assert.DoesNotContain(session.Rows, r => r.Selected);
    }

    [Fact]
    public void Net_sums_only_the_selected_rows()
    {
        var session = new FileAnalysisSession { Job = Job(Candidate(amount: -30m), Candidate(amount: 100m)) };
        session.SeedRows();

        Assert.Equal(70m, session.Net);
        session.ToggleRow(session.Rows[1]);
        Assert.Equal(-30m, session.Net);
    }

    [Theory]
    [InlineData("1234.50", 1234.50)]
    [InlineData("1,234.50", 1234.50)]
    [InlineData("-42", -42)]
    public void Amounts_parse_invariantly_with_thousands_separators_stripped(string typed, double expected)
    {
        var row = new FileAnalysisRow { Amount = 1m };

        FileAnalysisSession.SetAmount(row, typed);

        Assert.Equal((decimal)expected, row.Amount);
    }

    [Theory]
    [InlineData("not a number")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unparseable_amount_leaves_the_row_untouched(string? typed)
    {
        var row = new FileAnalysisRow { Amount = 7m };

        FileAnalysisSession.SetAmount(row, typed);

        Assert.Equal(7m, row.Amount);
    }

    [Fact]
    public void An_unparseable_date_leaves_the_row_untouched()
    {
        var row = new FileAnalysisRow { TransactionDate = new DateTime(2026, 1, 1) };

        FileAnalysisSession.SetDate(row, "2026-03-04");
        Assert.Equal(new DateTime(2026, 3, 4), row.TransactionDate);

        FileAnalysisSession.SetDate(row, "gibberish");
        Assert.Equal(new DateTime(2026, 3, 4), row.TransactionDate);
    }

    [Fact]
    public void The_import_body_carries_only_the_selected_rows_with_trimmed_references()
    {
        var session = new FileAnalysisSession { Job = Job(Candidate(), Candidate()) };
        session.SeedRows();
        session.Rows[1].Selected = false;
        session.Rows[0].Reference = "  REF-9  ";

        var request = session.BuildImportRequest();

        var candidate = Assert.Single(request.Candidates);
        Assert.Equal(session.Rows[0].CandidateId, candidate.CandidateId);
        Assert.Equal("REF-9", candidate.ExternalId);
    }

    [Fact]
    public void A_blank_reference_is_sent_as_null_not_an_empty_string()
    {
        var session = new FileAnalysisSession { Job = Job(Candidate()) };
        session.SeedRows();
        session.Rows[0].Reference = "   ";

        Assert.Null(Assert.Single(session.BuildImportRequest().Candidates).ExternalId);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Phase machine
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_phase_has_its_own_title_and_subtitle()
    {
        var session = new FileAnalysisSession();

        foreach (var phase in Enum.GetValues<FileAnalysisPhase>().Where(p => p != FileAnalysisPhase.Done))
        {
            session.SetPhase(phase);
            Assert.False(string.IsNullOrWhiteSpace(session.Title), $"{phase} has no title");
            Assert.False(string.IsNullOrWhiteSpace(session.Subtitle), $"{phase} has no subtitle");
        }
    }

    /// <summary>
    /// A stale per-action announcement must not be spoken over the new phase — the live region reads
    /// <c>ActionAnnounce</c> first, so a phase change has to clear it.
    /// </summary>
    [Fact]
    public void Changing_phase_clears_a_pending_action_announcement()
    {
        var session = new FileAnalysisSession();
        session.Announce("Linked merchant Rema 1000.");

        session.SetPhase(FileAnalysisPhase.Review);

        Assert.Null(session.ActionAnnounce);
    }

    [Fact]
    public void The_done_step_names_the_account_that_received_the_import()
    {
        var session = new FileAnalysisSession();

        session.SetImported("Everyday account");
        Assert.Equal(FileAnalysisPhase.Done, session.Phase);
        Assert.Contains("Everyday account", session.Subtitle);

        session.SetImported(null);
        Assert.Contains("your account", session.Subtitle);
    }

    [Theory]
    [InlineData(null, null, "empty")]
    [InlineData(0.9, 90, "info")]
    [InlineData(0.7, 70, "pending")]
    [InlineData(0.2, 20, "expense")]
    public void The_confidence_meter_bands_the_extraction_score(double? confidence, int? pct, string tone)
    {
        var band = FileAnalysisSession.ConfidenceBand((decimal?)confidence);

        Assert.Equal(pct, band.Pct);
        Assert.Equal(tone, band.Tone);
    }

    [Theory]
    [InlineData(-1234.5, "−1,234.50")]
    [InlineData(1234.5, "+1,234.50")]
    [InlineData(0, "+0.00")]
    public void Signed_totals_use_a_true_minus_sign_and_invariant_grouping(double value, string expected) =>
        Assert.Equal(expected, FileAnalysisSession.FormatSigned((decimal)value));
}
