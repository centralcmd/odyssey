namespace Odyssey.Core.Locations.Conversion;

/// <summary>
/// The one-time conversion of issue #380 §11 (step 2 of 3): every contact address, property address and
/// free-text event, series and entry place becomes a link to a <c>Location</c>. <c>MigrationRunner</c>
/// calls it between <see cref="ExpandMigration"/> and <see cref="ContractMigration"/>, and only while the
/// latter is pending, so a later upgrade never runs it again.
/// </summary>
public interface ILocationConversion
{
    /// <summary>The migration the conversion runs after (step 1).</summary>
    const string ExpandMigration = "UnifyLocationsExpand";

    /// <summary>The migration that runs after the conversion and drops the old columns (step 3).</summary>
    const string ContractMigration = "UnifyLocationsContract";

    Task<LocationConversionResult> ConvertAsync(CancellationToken cancellationToken);
}

/// <summary>What one run did — counts only, never a value.</summary>
/// <param name="Ran">False when the run returned without doing anything (old columns absent, or no relational provider).</param>
/// <param name="Created">Locations created.</param>
/// <param name="Linked">Rows linked to a location, created or matched.</param>
/// <param name="LeftAsText">Free-text places left as detail text because they look like a link or a credential.</param>
public sealed record LocationConversionResult(bool Ran, int Created, int Linked, int LeftAsText)
{
    public static readonly LocationConversionResult NotRun = new(false, 0, 0, 0);
}
