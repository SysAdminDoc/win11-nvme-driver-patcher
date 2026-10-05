using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Pins the receiver's deploy config. The KV binding once sat as a bare <c>kv_namespaces = [...]</c>
/// line under <c>[vars]</c>, which TOML reads as <c>vars.kv_namespaces</c>, so the Worker got a
/// variable by that name and no COMPAT binding. The rate limiters were on
/// <c>[[unsafe.bindings]]</c>, and wrangler floated to whatever npx resolved.
/// </summary>
public sealed class TelemetryReceiverConfigTests
{
    [Fact]
    public void WranglerToml_BindsKvAndBothLimitersAsRealBindings()
    {
        var lines = File.ReadAllLines(ReceiverPath("wrangler.toml"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

        Assert.DoesNotContain(lines, line => line.Contains("unsafe", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("kv_namespaces", StringComparison.Ordinal));
        Assert.Single(lines, "[[kv_namespaces]]");
        Assert.Contains("binding = \"COMPAT\"", lines);

        Assert.Equal(2, lines.Count(line => line == "[[ratelimits]]"));
        Assert.Contains("name = \"RATE_LIMITER\"", lines);
        Assert.Contains("name = \"SUMMARY_RATE_LIMITER\"", lines);
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i] != "[[ratelimits]]") continue;
            var block = lines.Skip(i + 1).TakeWhile(line => !line.StartsWith('[')).ToList();
            Assert.Contains(block, line => Regex.IsMatch(line, "^namespace_id = \"\\d+\"$"));
            Assert.Contains(block, line => Regex.IsMatch(line, "^simple = \\{ limit = \\d+, period = (10|60) \\}$"));
        }

        // Everything between [vars] and the next table header lands in vars.
        int vars = lines.IndexOf("[vars]");
        Assert.True(vars >= 0);
        var varKeys = lines.Skip(vars + 1).TakeWhile(line => !line.StartsWith('['))
            .Select(line => line.Split('=')[0].Trim())
            .ToList();
        Assert.Equal(["ALLOWED_ORIGINS"], varKeys);

        var date = lines.Single(line => line.StartsWith("compatibility_date", StringComparison.Ordinal));
        Assert.Matches("^compatibility_date = \"\\d{4}-\\d{2}-\\d{2}\"$", date);
    }

    [Fact]
    public void Wrangler_IsAnExactDevDependencyThatMatchesTheLockfile()
    {
        using var package = JsonDocument.Parse(File.ReadAllText(ReceiverPath("package.json")));
        var pinned = package.RootElement.GetProperty("devDependencies").GetProperty("wrangler").GetString()!;
        Assert.Matches(@"^\d+\.\d+\.\d+$", pinned);

        using var lockfile = JsonDocument.Parse(File.ReadAllText(ReceiverPath("package-lock.json")));
        var packages = lockfile.RootElement.GetProperty("packages");
        Assert.Equal(pinned, packages.GetProperty("").GetProperty("devDependencies").GetProperty("wrangler").GetString());
        Assert.Equal(pinned, packages.GetProperty("node_modules/wrangler").GetProperty("version").GetString());

        var npmrc = File.ReadAllLines(ReceiverPath(".npmrc")).Select(line => line.Trim()).ToList();
        Assert.Contains("save-exact=true", npmrc);
        Assert.Contains(npmrc, line => Regex.IsMatch(line, @"^min-release-age=[1-9]\d*$"));
    }

    private static string ReceiverPath(string name, [CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "packaging", "telemetry-receiver", name));
}
