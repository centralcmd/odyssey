using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Odyssey.Client.Tests;

/// <summary>
/// The client container's two start checks, run under <c>sh</c> (issue #370 AC 7, 10, 11). The image runs
/// them under busybox; these tests use the host's POSIX <c>sh</c>, which exercises the same script text.
/// Values reach the script through a real environment variable, never an argument, so a trailing newline
/// survives exactly as it would from compose.
/// </summary>
public class MapEntrypointScriptTests
{
    private static readonly string ScriptDirectory = Path.Combine(ClientSource.Root, "nginx", "docker-entrypoint.d");
    private static readonly string ValidateScript = Path.Combine(ScriptDirectory, "15-validate-map-origins.sh");
    private static readonly string AssertScript = Path.Combine(ScriptDirectory, "25-assert-rendered-config.sh");

    public static TheoryData<string> VectorNotes() => [.. LoadVectors().Select(v => v.Note)];

    [SkippableTheory]
    [MemberData(nameof(VectorNotes))]
    public void The_shell_check_gives_every_shared_vector_its_verdict(string note)
    {
        SkipWithoutShell();
        var vector = LoadVectors().Single(v => v.Note == note);

        var result = Run(ValidateScript, new() { ["MAP_ALLOWED_ORIGINS"] = vector.Value });

        Assert.True(
            (result.ExitCode == 0) == vector.Valid,
            $"{note}: expected {(vector.Valid ? "accept" : "reject")}, exit {result.ExitCode}: {result.Output}");
        if (!vector.Valid)
        {
            Assert.Contains("MAP_ALLOWED_ORIGINS", result.Output, StringComparison.Ordinal);
            Assert.Contains("map-csp-invalid", result.Output, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public void An_unset_variable_fails_and_an_empty_one_passes()
    {
        SkipWithoutShell();

        var unset = Run(ValidateScript, new() { ["MAP_ALLOWED_ORIGINS"] = null });
        var empty = Run(ValidateScript, new() { ["MAP_ALLOWED_ORIGINS"] = string.Empty });

        Assert.NotEqual(0, unset.ExitCode);
        Assert.Contains("not set", unset.Output, StringComparison.Ordinal);
        Assert.Equal(0, empty.ExitCode);
    }

    [SkippableFact]
    public void A_rejection_never_echoes_the_value()
    {
        SkipWithoutShell();

        var result = Run(ValidateScript, new() { ["MAP_ALLOWED_ORIGINS"] = "https://SECRETKEY123.example" });

        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("SECRETKEY123", result.Output, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void The_render_assertion_accepts_a_correctly_rendered_config()
    {
        SkipWithoutShell();

        var result = RunAssert(MapCspTemplateTests.Render("https://tiles.openfreemap.org"));

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [SkippableFact]
    public void The_render_assertion_accepts_the_off_switch_render()
    {
        SkipWithoutShell();

        Assert.Equal(0, RunAssert(MapCspTemplateTests.Render(string.Empty)).ExitCode);
    }

    public static TheoryData<string> BrokenRenders => ["missing", "no-csp", "commented-csp-only", "unrendered", "no-api-proxy"];

    [SkippableTheory]
    [MemberData(nameof(BrokenRenders))]
    public void The_render_assertion_refuses_a_broken_config(string kind)
    {
        SkipWithoutShell();
        var good = MapCspTemplateTests.Render("https://tiles.openfreemap.org");
        var config = kind switch
        {
            "missing" => null,
            "no-csp" => RemoveLines(good, "add_header Content-Security-Policy"),
            // The template's comments discuss the CSP; only the directive itself may satisfy the check.
            "commented-csp-only" => RemoveLines(good, "add_header Content-Security-Policy") + "\n# add_header Content-Security-Policy is set above\n",
            "unrendered" => File.ReadAllText(MapCspTemplateTests.TemplatePath),
            "no-api-proxy" => RemoveLines(good, "proxy_pass http://api:8080/;"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var result = RunAssert(config);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("map-config-unrendered", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_scripts_are_posix_sh_and_sort_around_the_stock_envsubst_step()
    {
        foreach (var script in new[] { ValidateScript, AssertScript })
        {
            var text = File.ReadAllText(script);
            Assert.StartsWith("#!/bin/sh\n", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        }

        var names = new[] { Path.GetFileName(ValidateScript), "20-envsubst-on-templates.sh", Path.GetFileName(AssertScript) };
        Assert.Equal(names, names.Order(StringComparer.Ordinal));
    }

    private static string RemoveLines(string text, string containing) =>
        string.Join('\n', text.Split('\n').Where(line => !line.Contains(containing, StringComparison.Ordinal)));

    private static (int ExitCode, string Output) RunAssert(string? config)
    {
        var directory = Directory.CreateTempSubdirectory("odyssey-map-render-");
        try
        {
            if (config is not null)
                File.WriteAllText(Path.Combine(directory.FullName, "default.conf"), config);

            return Run(AssertScript, new() { ["NGINX_ENVSUBST_OUTPUT_DIR"] = directory.FullName });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static (int ExitCode, string Output) Run(string script, Dictionary<string, string?> environment)
    {
        var start = new ProcessStartInfo("/bin/sh", [script])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment.Remove("MAP_ALLOWED_ORIGINS");
        start.Environment.Remove("NGINX_ENVSUBST_OUTPUT_DIR");
        foreach (var (key, value) in environment)
        {
            if (value is null)
                start.Environment.Remove(key);
            else
                start.Environment[key] = value;
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Path.GetFileName(script)} did not finish.");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    // Only an ABSENT prerequisite skips (issue #257): a Windows host has no /bin/sh.
    private static void SkipWithoutShell() =>
        Skip.IfNot(File.Exists("/bin/sh"), "No /bin/sh on this host; the entrypoint scripts run only in the Linux client image.");

    internal static IReadOnlyList<(string Value, bool Valid, string Note)> LoadVectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MapOriginVectors.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("vectors").EnumerateArray()
            .Select(v => (v.GetProperty("value").GetString()!, v.GetProperty("valid").GetBoolean(), v.GetProperty("note").GetString()!))
            .ToList();
    }
}
