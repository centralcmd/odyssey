using Microsoft.AspNetCore.Components;
using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

public partial class ContractsCard
{
    // ── Record-card presentation ──────────────────────────────────────────────────

    /// <summary>The Status tile's value tint, from the same registry the status chip reads, so the
    /// chip in the header and the tile in the body can never disagree.</summary>
    private static OdsInfoTileTone StatusTone(string chipTone) => chipTone switch
    {
        "income" => OdsInfoTileTone.Income,
        "info" => OdsInfoTileTone.Info,
        "expense" => OdsInfoTileTone.Expense,
        "pending" => OdsInfoTileTone.Pending,
        _ => OdsInfoTileTone.Muted,
    };

    /// <summary>The date the current state began — a status is a reading of the record at a moment,
    /// so it carries when that moment was. Null where the record has no date to point at.</summary>
    private static string? StatusFoot(ExistingContract c, bool oneOff) => c.Status switch
    {
        ContractStatus.Archived => c.Archived is { } a ? $"since {OdsDates.Long(a)}" : null,
        ContractStatus.Draft => "not yet marked ready for signature",
        ContractStatus.Ready => c.Ready is { } r
            ? $"ready since {OdsDates.Long(r)} — waiting on a signature"
            : "waiting on a signature",
        ContractStatus.Paused => c.Paused is { } p ? $"since {OdsDates.Long(p)}" : null,
        ContractStatus.Expired => c.EndDate is { } e ? $"since {OdsDates.Long(e)}" : null,
        ContractStatus.Upcoming => c.StartDate is { } s ? $"starts {OdsDates.Long(s)}" : null,
        _ when oneOff => c.CompletionDate is { } d ? $"completed {OdsDates.Long(d)}" : null,
        _ => c.StartDate is { } s ? $"since {OdsDates.Long(s)}" : null,
    };

    /// <summary>
    /// The row action menu, permission-gated. Archive is offered with its reason rather than hidden
    /// when it does not apply: the lifecycle is ordered (only an ended contract can be archived), and
    /// a disabled item that says why is more use than an item that silently is not there. The server
    /// enforces the same rule.
    ///
    /// <para>
    /// "Ended" is not the derived <see cref="ContractStatus.Expired"/>: a delivered one-off is over
    /// but reads Active, so completion counts too. The test itself is
    /// <see cref="ContractLifecycle.HasEnded"/>, shared with the service.
    /// </para>
    /// </summary>
    private IReadOnlyList<OdsMenuItem> RowActions(ContractListItem c, bool archived)
    {
        // The SAME predicate the service enforces, not a second copy of its boundaries — it lives in
        // Odyssey.Dtos precisely so the disabled action and the 400 can never disagree.
        var hasEnded = ContractLifecycle.HasEnded(c.EndDate, c.CompletionDate, Today);
        var items = new List<OdsMenuItem>();

        // On file but not in force — the single predicate the money roll-ups gate on, used here so
        // the menu and the server can never disagree about which contracts are unsigned.
        var unsigned = ContractStatusOrder.IsUnsigned(c.Status);

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "edit",
                Label = "Edit contract",
                OnClick = EventCallback.Factory.Create(this, () => EditClicked(c)),
            });

            // The signature path (issue #145), FIRST while it is the thing the contract is waiting
            // on. Offered in lifecycle order, one step at a time: a contract already signed is never
            // offered "Mark ready". The dates are also editable in the dialog, for backdating.
            if (c.Signed is null && c.Ready is null)
            {
                items.Add(new OdsMenuItem
                {
                    Icon = "draw",
                    Label = "Mark ready for signature",
                    OnClick = EventCallback.Factory.Create(this, () => MarkReady(c)),
                });
            }
            else if (c.Signed is null)
            {
                items.Add(new OdsMenuItem
                {
                    Icon = "history_edu",
                    Label = "Mark signed",
                    OnClick = EventCallback.Factory.Create(this, () => MarkSigned(c)),
                });
            }

            // Offered whenever EITHER stamp exists, in any state — clearing is never refused, which
            // is what stops an archived or expired contract being stranded holding one.
            if (c.Signed is not null || c.Ready is not null)
            {
                items.Add(new OdsMenuItem
                {
                    Icon = "undo",
                    Label = c.Signed is not null ? "Unsign" : "Clear ready date",
                    OnClick = EventCallback.Factory.Create(this, () => Unsign(c)),
                });
            }

            // Pause is enterable from Active ALONE, so the action is ABSENT everywhere else — as
            // every unavailable action now is (design system · components/Menu). The unsigned case
            // used to be the exception, offered dimmed with "sign it first" as an instruction the
            // reader could act on; that whole treatment is retired, and where a precondition still
            // needs stating the RECORD states it, not a menu row nobody has opened. Resume is offered
            // wherever a stamp exists, in any state — clearing a pause is never refused, which is
            // what stops an archived or expired contract being stranded holding one.
            if (c.Status == ContractStatus.Active)
            {
                items.Add(new OdsMenuItem
                {
                    Icon = "pause_circle",
                    Label = "Pause",
                    OnClick = EventCallback.Factory.Create(this, () => TogglePause(c)),
                });
            }
            else if (unsigned && c.Paused is null)
            {
                // Only a signed contract in force can be paused, so the item is absent rather than
                // dimmed with a reason (design system · components/Menu). The status chip on the
                // record head already says the contract is unsigned.
                items.Add(new OdsMenuItem
                {
                    Icon = "pause_circle",
                    Label = "Pause",
                    Disabled = true,
                });
            }
            else if (c.Paused is not null)
            {
                items.Add(new OdsMenuItem
                {
                    Icon = "play_circle",
                    Label = "Resume",
                    OnClick = EventCallback.Factory.Create(this, () => TogglePause(c)),
                });
            }
            // None of the three is gated on the archive state. Archiving hides a contract from the
            // default list; it does not lock it, and the closing rent, the final invoice and the
            // handover note are exactly what get recorded after an agreement has ended. The server
            // refuses none of these writes on an archived contract either.
            items.Add(new OdsMenuItem
            {
                Icon = "group_add",
                Label = "New party",
                OnClick = EventCallback.Factory.Create(this, () => AddParty(c.ContractId)),
            });

            items.Add(new OdsMenuItem
            {
                Icon = "§",
                Label = "New term",
                OnClick = EventCallback.Factory.Create(this, () => AddTerm(c.ContractId)),
            });
        }

        if (CanAttachDocuments)
        {
            // Ungated like New party, New term and New event: ContractService.AttachFile carries no
            // archive guard, so an archived contract takes a document exactly as any other does.
            items.Add(new OdsMenuItem
            {
                Icon = "attach_file",
                Label = "Attach documents",
                OnClick = EventCallback.Factory.Create(this, () => AttachDocument(c.ContractId)),
            });
        }

        // After Attach documents, the design system's order: the three that shape the agreement, then
        // the document that evidences it, then the log of what has happened to it. Gated on update
        // like New party and New term, so it sits in a block of its own.
        if (_canUpdate)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "history",
                Label = "New event",
                OnClick = EventCallback.Factory.Create(this, () => AddEvent(c.ContractId)),
            });
        }

        // Beside Copy ID, and only when a number is on file — absent, not disabled, for a contract
        // without one (issue #181).
        if (!string.IsNullOrWhiteSpace(c.ReferenceNumber))
        {
            var referenceNumber = c.ReferenceNumber;
            items.Add(new OdsMenuItem
            {
                Icon = "tag",
                Label = "Copy reference number",
                TrailingIcon = "content_copy",
                OnClick = EventCallback.Factory.Create(this,
                    () => Clipboard.CopyAsync(referenceNumber, OdsCopied.Value("Reference number"))),
            });
        }

        items.Add(OdsMenuItem.CopyId(EventCallback.Factory.Create(this, () => CopyId(c.ContractId))));

        if (_canUpdate)
        {
            items.Add(new OdsMenuItem { Divider = true });
            // An ENDED or an UNSIGNED contract can be archived (issue #145 widened the server rule):
            // abandoning a negotiation is the likeliest reason to archive a draft, and a draft
            // typically has no end date at all, so the un-widened rule would offer a step the reader
            // could never take.
            items.Add(hasEnded || archived || unsigned
                ? new OdsMenuItem
                {
                    Icon = archived ? "unarchive" : "inventory_2",
                    Label = archived ? "Restore" : "Archive",
                    OnClick = EventCallback.Factory.Create(this, () => ToggleArchive(c)),
                }
                // A contract that is neither ended, archived nor unsigned cannot be archived, so
                // the item is absent rather than dimmed with a reason.
                : new OdsMenuItem
                {
                    Icon = "inventory_2",
                    Label = "Archive",
                    Disabled = true,
                });
        }

        if (_canDelete)
        {
            items.Add(new OdsMenuItem
            {
                Icon = "delete",
                Label = "Delete",
                Danger = true,
                OnClick = EventCallback.Factory.Create(this, () => ConfirmDelete(c)),
            });
        }

        return items;
    }


    // ── Collapsed headline figure (mirrors the design's conHeadline) ──────────────
    private (bool HasValue, string Value, string Word, string Cls) Headline(ContractListItem c)
    {
        // Paused: a countdown is meaningless while nothing is running, so the headline says when the
        // pause began instead. Checked HERE, above the one-off branch, for exactly the reason the
        // server applies Paused to the RESULT of its derivation — the one-off branch returns early,
        // so a paused settled one-off would otherwise keep counting down to a completion it reached.
        if (c.Status == ContractStatus.Paused)
        {
            return c.Paused is { } pausedAt
                ? (true, OdsDates.Long(pausedAt), "paused", "paused")
                : (false, "Paused", "paused", "paused");
        }

        // Unsigned (issue #145): the term's dates describe something nobody has agreed to, so
        // counting down to them would assert a commitment that does not exist. The headline says
        // where the signature got to instead. Checked HERE, above the one-off branch, for exactly the
        // reason the server puts the layer above its date chain — the one-off branch returns early,
        // so a check placed after it would never run for an unsigned one-off.
        if (c.Status == ContractStatus.Draft)
        {
            // No date to point at, by definition — a draft is the state of having neither stamp — so
            // the figure is the word itself, the same shape the open-ended and archived fallbacks
            // already take rather than inventing an anchor.
            return (false, "Draft", "not yet ready for signature", "");
        }

        if (c.Status == ContractStatus.Ready)
        {
            return c.Ready is { } readyAt
                ? (true, OdsDates.Long(readyAt), "ready for signature", "soon")
                : (false, "Ready", "ready for signature", "soon");
        }

        // One-off contracts headline on their completion date (no ongoing term).
        if (c.CompletionDate is { } completion)
        {
            if (c.Status == ContractStatus.Archived)
            {
                return (true, OdsDates.Long(completion), "archived", "archived");
            }

            var toGo = (completion.Date - Today).Days;
            var oneOffWord = toGo > 0
                ? $"completes in {toGo} day{(toGo == 1 ? "" : "s")}"
                : "one-off";
            return (true, OdsDates.Long(completion), oneOffWord, "");
        }

        switch (c.Status)
        {
            case ContractStatus.Upcoming when c.StartDate is { } start:
                {
                    var days = (start.Date - Today).Days;
                    var word = days <= 0 ? "starts today" : $"starts in {days} day{(days == 1 ? "" : "s")}";
                    return (true, OdsDates.Long(start), word, "");
                }
            case ContractStatus.Expired when c.EndDate is { } end:
                {
                    var days = (Today - end.Date).Days;
                    var word = days <= 0 ? "expired today" : $"expired {days} day{(days == 1 ? "" : "s")} ago";
                    return (true, OdsDates.Long(end), word, "expired");
                }
            case ContractStatus.Archived:
                {
                    var anchor = c.EndDate ?? c.StartDate;
                    return anchor is { } a
                        ? (true, OdsDates.Long(a), "archived", "archived")
                        : (false, "Archived", "archived", "archived");
                }
            default: // Active
                if (c.EndDate is { } activeEnd)
                {
                    var days = (activeEnd.Date - Today).Days;
                    var soon = days <= EndingWindowDays;
                    var word = days <= 0 ? "ends today" : $"ends in {days} day{(days == 1 ? "" : "s")}";
                    return (true, OdsDates.Long(activeEnd), word, soon ? "soon" : "");
                }
                return (false, "Open-ended", "no end date", "");
        }
    }
}
