using System.Text.RegularExpressions;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The client image lines the map's CSP depends on (issue #370). <see cref="MapCspTemplateTests"/> renders the
/// template the way the image does, but only these lines make the image actually do that: drop the filter and
/// envsubst blanks <c>$host</c>, <c>$scheme</c> and <c>$forwarded_proto</c> while every fast tier stays green.
/// </summary>
public class ClientImageWiringTests
{
    private static readonly Lazy<string> Dockerfile = new(() =>
        Regex.Replace(File.ReadAllText(Path.Combine(ClientSource.Root, "Dockerfile")), @"(?m)^\s*#.*$", string.Empty));

    [Fact]
    public void Substitution_is_filtered_to_the_map_origin_variable_alone()
    {
        Assert.Matches(@"(?m)^ENV NGINX_ENVSUBST_FILTER=\^MAP_ALLOWED_ORIGINS\\\$\s*$", Dockerfile.Value);
    }

    [Fact]
    public void The_template_and_the_start_checks_are_copied_and_the_welcome_config_is_deleted()
    {
        var text = Dockerfile.Value;

        Assert.Matches(@"(?m)^RUN rm -f /etc/nginx/conf\.d/default\.conf\s*$", text);
        Assert.Matches(@"(?m)^COPY Odyssey\.Client/nginx/templates/ /etc/nginx/templates/\s*$", text);
        Assert.Matches(@"(?m)^COPY --chmod=755 Odyssey\.Client/nginx/docker-entrypoint\.d/ /docker-entrypoint\.d/\s*$", text);
        // A direct COPY into conf.d would collide with the rendered file.
        Assert.DoesNotMatch(@"(?m)^COPY\s.*\s/etc/nginx/conf\.d", text);
    }

    [Fact]
    public void The_rm_runs_in_the_final_stage_before_the_template_copy()
    {
        var text = Dockerfile.Value;
        var final = text.IndexOf("AS final", StringComparison.Ordinal);
        var rm = text.IndexOf("RUN rm -f /etc/nginx/conf.d/default.conf", StringComparison.Ordinal);

        Assert.True(final >= 0 && rm > final, "The welcome config must be deleted in the final (nginx) stage.");
    }

    [Fact]
    public void Shell_scripts_are_pinned_to_lf_line_endings()
    {
        var attributes = File.ReadAllLines(ClientSource.Sibling(".gitattributes"));

        Assert.Contains(attributes, line => Regex.IsMatch(line, @"^\*\.sh\s+text\s+eol=lf\s*$"));
    }
}
