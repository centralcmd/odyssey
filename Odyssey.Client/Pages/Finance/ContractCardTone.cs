using Odyssey.Client.Components;
using Odyssey.Dtos.Finance;

namespace Odyssey.Client.Pages.Finance;

/// <summary>
/// The colour a contract's record card takes. The headline figure and the header mark read ONE derived
/// tone; the type accent still colours everything inside the card.
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
    /// The card root's classes: <c>con-tone-*</c> tints the header mark with the figure's tone, and
    /// <c>con-unsigned</c> dashes the edge of a Draft or Ready row.
    /// </summary>
    public static string CardClass(OdsRecordFigureTone tone, bool unsigned) =>
        $"con-card con-tone-{tone.ToString().ToLowerInvariant()}{(unsigned ? " con-unsigned" : "")}";

    /// <summary>
    /// The Signed tile's tint. A signature on an agreement that has ended is history, not good news,
    /// so the tile drops its tone once the status has.
    /// </summary>
    public static OdsInfoTileTone Signed(ContractStatus status) =>
        status is ContractStatus.Expired or ContractStatus.Archived ? OdsInfoTileTone.Default : OdsInfoTileTone.Income;
}
