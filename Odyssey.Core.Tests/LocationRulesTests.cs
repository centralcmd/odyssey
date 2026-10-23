using System.ComponentModel.DataAnnotations;
using Odyssey.Dtos;
using Odyssey.Dtos.Journal;
using Xunit;

namespace Odyssey.Core.Tests;

/// <summary>The shared location rules in <c>Odyssey.Dtos</c> (issue #350 §7.10, §8).</summary>
public class LocationRulesTests
{
    [Theory]
    [InlineData("Europe/Oslo", true)]
    [InlineData("Europe/Kyiv", true)]
    [InlineData("America/Argentina/Buenos_Aires", true)]
    [InlineData("Etc/GMT+5", true)]
    [InlineData("UTC", true)]
    [InlineData("Mars/Base", false)]
    [InlineData("../../etc", false)]
    [InlineData("Europe/../Oslo", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("posixrules", false)]
    [InlineData("posix/Europe/Oslo", false)]
    [InlineData("right/Europe/Oslo", false)]
    [InlineData("Local", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IanaTimeZones_accepts_resolvable_ids_of_the_safe_shape_only(string? id, bool valid) =>
        Assert.Equal(valid, IanaTimeZones.IsValid(id));

    [Fact]
    public void IanaTimeZones_rejects_an_id_longer_than_64() =>
        Assert.False(IanaTimeZones.IsValid("Europe/" + new string('A', 60)));

    [Theory]
    [InlineData(LocationSource.Manual, null, true)]
    [InlineData(LocationSource.Manual, "123", false)]
    [InlineData(LocationSource.Kartverket, "3303-6060-1A", true)]
    [InlineData(LocationSource.Kartverket, "552132", true)]
    [InlineData(LocationSource.Kartverket, "a/b", false)]
    [InlineData(LocationSource.Kartverket, "a b", false)]
    [InlineData(LocationSource.OpenMeteo, "2267057", true)]
    [InlineData(LocationSource.OpenMeteo, "22a", false)]
    [InlineData(LocationSource.OpenMeteo, null, true)]
    public void Source_references_must_have_their_providers_shape(LocationSource source, string? reference, bool valid) =>
        Assert.Equal(valid, LocationRules.IsValidReference(source, reference));

    [Theory]
    [InlineData("  Storgata   1 ", "storgata 1")]
    [InlineData("Å", "å")]
    [InlineData("ÅL", "ål")]
    public void NormalizeQuery_trims_collapses_composes_and_lower_cases(string input, string expected) =>
        Assert.Equal(expected, LocationRules.NormalizeQuery(input));

    [Theory]
    [InlineData(LocationSearchRegion.Norway, "ål", true)]
    [InlineData(LocationSearchRegion.Norway, " å ", false)]
    [InlineData(LocationSearchRegion.World, "ål", false)]
    [InlineData(LocationSearchRegion.World, "ålo", true)]
    public void The_search_request_applies_the_per_region_minimum(LocationSearchRegion region, string query, bool valid)
    {
        var request = new LocationSearchRequest { Query = query, Region = region };
        var results = new List<ValidationResult>();

        var ok = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.Equal(valid, ok);
    }
}
