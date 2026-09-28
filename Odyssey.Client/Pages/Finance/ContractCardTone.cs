using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The colour a contract's record card takes. The headline figure reads one derived tone; the header mark
/// and everything inside the card take the contract TYPE's accent.
/// </summary>
public static class ContractCardTone
{
    /// <summary>
    /// The headline figure's colour role. A lapsed term reads expense, one ending inside the window or
    /// one suspended reads pending; everything else takes the status chip's own tone, so an outline
    /// status (Draft, Archived) reads muted rather than in the default ink.
    /// </summary>
    public static OdsRecordFigureTone Headline(string cls, ContractStatus status) => cls switch
    {
        "expired" => OdsRecordFigureTone.Expense,
        "soon" or "paused" => OdsRecordFigureTone.Pending,
        _ => OdsContractStatus.Meta(status).Tone switch
        {
            "income" => OdsRecordFigureTone.Income,
            "expense" => OdsRecordFigureTone.Expense,
            "pending" => OdsRecordFigureTone.Pending,
            "info" => OdsRecordFigureTone.Info,
            _ => OdsRecordFigureTone.Muted,
        },
    };

    /// <summary>
    /// The card root's classes: <c>con-unsigned</c> dashes the edge of a Draft or Ready row. The header
    /// mark no longer follows the figure's tone — the design system retired that tint, so the mark reads
    /// the type accent like the rest of the card.
    /// </summary>
    public static string CardClass(bool unsigned) =>
        unsigned ? "con-card con-unsigned" : "con-card";

    /// <summary>
    /// The Signed tile's tint. A signature on an agreement that has ended is history, not good news,
    /// so the tile drops its tone once the status has.
    /// </summary>
    public static OdsInfoTileTone Signed(ContractStatus status) =>
        status is ContractStatus.Expired or ContractStatus.Archived ? OdsInfoTileTone.Default : OdsInfoTileTone.Income;
}
