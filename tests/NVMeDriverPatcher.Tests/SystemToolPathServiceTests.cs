using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class SystemToolPathServiceTests
{
    [Theory]
    [InlineData("fsutil.exe")]
    [InlineData("mountvol.exe")]
    [InlineData("manage-bde.exe")]
    [InlineData("bcdedit.exe")]
    [InlineData("pnputil.exe")]
    [InlineData("schtasks.exe")]
    [InlineData("shutdown.exe")]
    public void Resolve_ReturnsAnExistingAbsoluteSystem32Path(string tool)
    {
        var resolved = SystemToolPathService.Resolve(tool);

        Assert.True(Path.IsPathFullyQualified(resolved), $"{tool} did not resolve to an absolute path.");
        Assert.True(File.Exists(resolved), $"{tool} resolved to a path that does not exist: {resolved}");
        Assert.Equal(tool, Path.GetFileName(resolved), ignoreCase: true);
    }

    [Fact]
    public void PowerShell_ResolvesToWindowsPowerShellUnderSystem32()
    {
        var resolved = SystemToolPathService.PowerShell;

        Assert.True(Path.IsPathFullyQualified(resolved));
        Assert.True(File.Exists(resolved), $"powershell.exe not found at {resolved}");
        Assert.Contains(@"WindowsPowerShell\v1.0", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_RejectsAnEmptyToolName(string tool) =>
        Assert.Throws<ArgumentException>(() => SystemToolPathService.Resolve(tool));

    /// <summary>
    /// Any <c>"tool.exe"</c> literal that is not wrapped in a <see cref="SystemToolPathService"/>
    /// resolve call. The previous detector only matched a literal sitting directly inside
    /// <c>new ProcessStartInfo(</c>, so it stayed green while the watchdog shipped
    /// <c>RunProcess("sc.exe", args)</c> — one level of indirection was enough to hide the defect.
    /// Matching on the literal instead of on the call shape removes that escape hatch.
    /// </summary>
    private static readonly Regex UnresolvedToolLiteral = new(
        @"(?<!SystemToolPathService\.Resolve\()""[A-Za-z0-9_.\-]+\.exe""",
        RegexOptions.Compiled);

    // The tools this suite knows, named without ".exe". CreateProcess appends ".exe" to a bare name
    // and searches the same directories, so "reg" runs a planted reg.exe just as "reg.exe" would.
    private const string KnownBareTool =
        "reg|regedit|schtasks|dism|bcdedit|powershell|pwsh|pnputil|sc|wevtutil|fsutil|mountvol|" +
        "manage-bde|shutdown|reagentc|verifier|wpr|cmd|explorer|notepad|diskpart|devcon";

    private static readonly Regex UnresolvedBareToolLiteral = new(
        @"(?<!SystemToolPathService\.Resolve\()""(?i:" + KnownBareTool + @")""",
        RegexOptions.Compiled);

    /// <summary>
    /// What may sit immediately before a literal for it to name an executable without launching it:
    /// an asset/file name, a fallback for a path this process already owns, or a name being
    /// compared against. It is matched against the text before that one literal (anchored at its
    /// end), not the whole line, so a launch site can't hide behind a non-launch call elsewhere on
    /// the same line. Parenthesis nesting is limited to one level, which is enough for the call
    /// shapes in src.
    /// </summary>
    private static readonly Regex NonLaunchLiteralPrefix = new(
        @"(Environment\.ProcessPath\s*\?\?\s*" +
        @"|(Path\.Combine|Directory\.GetFiles|Directory\.Enumerate\w*|File\.Exists)\((?:[^()]|\([^()]*\))*" +
        @"|const\s+string\s+\w+\s*=\s*" +
        @"|\.Equals\((?:[^()]|\([^()]*\))*" +
        @"|(==|!=)\s*)$",
        RegexOptions.Compiled);

    [Fact]
    public void NoShippedSourceLaunchesAToolByBareName()
    {
        // Both shipped executables carry a requireAdministrator manifest, so a bare tool name
        // resolves through the executable directory, the current directory and PATH, and would run
        // a planted binary elevated. This is the same shadowing that made the recovery kit's bare
        // `find` hang the integrity gate.

        // Self-check: the detector must fire on every shape it is meant to catch, including the two
        // that the pre-2026-08 regex missed.
        Assert.True(IsOffendingLine(@"var psi = new ProcessStartInfo(""fsutil.exe"")"));
        Assert.True(IsOffendingLine(@"=> RunProcess(""sc.exe"", args);"));            // watchdog defect
        Assert.True(IsOffendingLine(@"var bcd = RunCapture(""bcdedit.exe"", args);")); // WinRE probe defect
        Assert.True(IsOffendingLine(@"await runner(""dism.exe"","));                   // WinPE/WinRE defect
        // The same tools without ".exe", which the .exe-only literal match never saw.
        Assert.True(IsOffendingLine(@"var psi = new ProcessStartInfo(""schtasks"")"));
        Assert.True(IsOffendingLine(@"=> RunProcess(""sc"", args);"));
        Assert.True(IsOffendingLine(@"await runner(""PowerShell"", args,"));

        // ...and must stay quiet on a resolved launch and on the non-launch shapes.
        Assert.False(IsOffendingLine(@"if (document.Root?.Name.LocalName != ""PnpUtil"")"));
        Assert.False(IsOffendingLine(@"var category = ""registry"";"));
        Assert.False(IsOffendingLine(@"new ProcessStartInfo(SystemToolPathService.Resolve(""fsutil.exe""))"));
        Assert.False(IsOffendingLine(@"var exe = Environment.ProcessPath ?? ""NVMeDriverPatcher.exe"";"));
        Assert.False(IsOffendingLine(@"var p = Path.Combine(dir, ""diskspd.exe"");"));
        Assert.False(IsOffendingLine(@"if (!string.Equals(Path.GetFileName(exe), ""app.exe"", StringComparison.OrdinalIgnoreCase))"));
        Assert.False(IsOffendingLine(@"return File.Exists(Path.Combine(sysDir, ""wpr.exe""));"));

        // A launch must not hide behind a non-launch call elsewhere on its line. The old detector
        // exempted the whole line, so this one passed it.
        const string counterExample = @"new ProcessStartInfo(""dism.exe"") { WorkingDirectory = Path.Combine(dir) }";
        Assert.True(IsOffendingLine(counterExample));
        Assert.True(IsOffendingLine(@"Run(""sc.exe"", Path.Combine(dir, ""x.txt""));"));
        var oldLineLevelExemption = new Regex(
            @"(Environment\.ProcessPath|Path\.Combine|Directory\.GetFiles|Directory\.Enumerate|File\.Exists|const string|\.Equals\(|GetFileName\()");
        Assert.True(UnresolvedToolLiteral.IsMatch(counterExample) && oldLineLevelExemption.IsMatch(counterExample),
            "the counter-example must be one the old whole-line exemption would have waved through");

        var offenders = ShippedSourceFiles("src")
            .SelectMany(path => File.ReadAllLines(path)
                .Select((line, index) => (path, line, number: index + 1))
                .Where(entry => IsOffendingLine(entry.line))
                .Select(entry => $"{Path.GetFileName(entry.path)}:{entry.number}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These lines name a tool without resolving it; route them through SystemToolPathService: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// A test that starts a process from a bare name: a ProcessStartInfo or Process.Start call whose
    /// first argument is a string literal, or a FileName assignment (initializer or a later
    /// <c>psi.FileName = ...</c>) of an executable literal, with or without its extension. A bare
    /// name resolves through the current directory and PATH, so a planted binary in the test output
    /// folder would run in place of the real tool. Only launch-shaped uses count: asset names,
    /// fixtures and assertions that merely mention an .exe are fine. Names held in a variable are
    /// <see cref="NoSourceStartsAProcessFromAnUnresolvedTarget"/>'s job.
    /// </summary>
    private static readonly Regex BareNameLaunch = new(
        @"(ProcessStartInfo\(\s*|Process\.Start\(\s*)""(?<tool>[^""\\/:]+)""" +
        @"|(?<!\w)FileName\s*=\s*""(?<tool>[^""\\/:.]+(?:\.(?:exe|com|cmd|bat))?)""",
        RegexOptions.Compiled);

    // Fixtures that must carry a bare name, as (file, tool). node has no fixed install path, and the
    // test skips itself when it is missing, so it is looked up on PATH on purpose.
    private static readonly (string File, string Tool)[] BareNameAllowlist =
    [
        ("TelemetryReceiverSummaryTests.cs", "node"),
    ];

    [Fact]
    public void NoTestLaunchesAToolByBareName()
    {
        // Self-check the detector against each launch shape, and against shapes that must stay quiet.
        Assert.Matches(BareNameLaunch, "var startInfo = new ProcessStartInfo(\"powershell.exe\")");
        Assert.Matches(BareNameLaunch, "process.StartInfo = new System.Diagnostics.ProcessStartInfo(\n    \"cmd.exe\", args)");
        Assert.Matches(BareNameLaunch, "Process.Start(\"explorer.exe\");");
        Assert.Matches(BareNameLaunch, "new ProcessStartInfo { FileName = \"sc.exe\" }");
        Assert.Matches(BareNameLaunch, "new ProcessStartInfo { FileName = \"reg\" }");      // no extension
        Assert.Matches(BareNameLaunch, "startInfo.FileName = \"schtasks\";");             // separate statement
        Assert.DoesNotMatch(BareNameLaunch, "const string ManifestFileName = \"update.exe\";");
        Assert.DoesNotMatch(BareNameLaunch, "new ProcessStartInfo(SystemToolPathService.PowerShell)");
        Assert.DoesNotMatch(BareNameLaunch, "new ProcessStartInfo(SystemToolPathService.Resolve(\"cmd.exe\"), args)");
        Assert.DoesNotMatch(BareNameLaunch, "new ProcessStartInfo(@\"C:\\Windows\\System32\\cmd.exe\")");
        Assert.DoesNotMatch(BareNameLaunch, "FileName = \"compat.json\"");
        Assert.DoesNotMatch(BareNameLaunch, "Assert.Equal(\"NVMeDriverPatcher.exe\", name);");

        var offenders = ShippedSourceFiles("tests")
            .SelectMany(path => BareNameLaunch.Matches(File.ReadAllText(path))
                .Select(m => (File: Path.GetFileName(path), Tool: m.Groups["tool"].Value)))
            .Where(hit => !BareNameAllowlist.Contains(hit))
            .Select(hit => $"{hit.File} ({hit.Tool})")
            .Distinct()
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These tests launch a tool by bare name; use SystemToolPathService.Resolve/.PowerShell, or add a justified allowlist entry: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// What a process is started from: the first argument of <c>new ProcessStartInfo(...)</c>, a
    /// string literal passed to <c>Process.Start(...)</c>, or any <c>FileName = ...</c> assignment.
    /// The literal-shaped detectors above miss a name that arrives through a variable or a later
    /// <c>psi.FileName = tool;</c>, so here every target has to be visibly trusted. Parenthesis
    /// nesting is limited to two levels, enough for <c>SystemToolPathService.Resolve(Path.Combine(...))</c>.
    /// </summary>
    private static readonly Regex LaunchTarget = new(
        @"new\s+(?:System\.Diagnostics\.)?ProcessStartInfo\s*\(\s*(?<value>(?:[^(),]|\((?:[^()]|\([^()]*\))*\))+)[,)]" +
        @"|Process\.Start\(\s*(?<value>@?""(?:[^""]|"""")*"")" +
        @"|(?<!\w)FileName\s*=(?![=>])\s*(?<value>[^,;}\r\n]+)",
        RegexOptions.Compiled);

    // A SystemToolPathService call (optionally namespace-qualified) or a literal fully qualified path.
    private static readonly Regex TrustedLaunchTarget = new(
        @"^(?:(?:[\w]+\.)*SystemToolPathService\.|@?""[A-Za-z]:\\)",
        RegexOptions.Compiled);

    // Launch targets held in a variable whose origin was checked by hand, as (repo-relative file,
    // target expression, where the value comes from). An entry that stops matching fails the test,
    // so the list can't outlive the code it vouches for.
    private static readonly (string File, string Target, string Origin)[] LaunchTargetAllowlist =
    [
        ("src/NVMeDriverPatcher.Watchdog/Program.cs", "executable",
            "RunProcess's parameter; its only caller passes SystemToolPathService.Resolve(\"sc.exe\")"),
        ("src/NVMeDriverPatcher.Tray/Program.cs", "exe",
            "Path.Combine of AppContext.BaseDirectory (or its parent) and NVMeDriverPatcher.exe"),
        ("src/NVMeDriverPatcher.Core/Services/BenchmarkService.cs", "exePath",
            "the hash-pinned diskspd.exe InstallDiskSpdAsync places under the working directory"),
        ("src/NVMeDriverPatcher.Core/Services/ViVeToolService.cs", "exePath",
            "Path.Combine of the verified payload directory and ViVeTool.exe"),
        ("src/NVMeDriverPatcher.Core/Services/VerifiedDownloader.cs", "signtool",
            "ResolveSigntool, which only returns absolute Windows Kits paths that exist"),
        ("src/NVMeDriverPatcher.Core/Services/WinReDriverInjectionService.cs", "file",
            "RunProcessAsync's parameter; callers pass SystemToolPathService.Resolve(\"dism.exe\") or a plan step whose Exe defaults to it"),
        ("src/NVMeDriverPatcher.Core/Services/WinReBcdPrepService.cs", "exe",
            "RunCapture's parameter; every caller passes SystemToolPathService.Resolve(...)"),
        ("src/NVMeDriverPatcher.Core/Services/WinPERecoveryBuilderService.cs", "file",
            "RunProcessAsync's parameter; every caller passes SystemToolPathService.Resolve(...)"),
        ("src/NVMeDriverPatcher/ViewModels/MainViewModel.cs", "url",
            "an https URL opened through the shell after IsAllowedBrowserUrl, not a tool"),
        ("src/NVMeDriverPatcher.Core/Services/DataFileProvenanceService.cs", "fileName",
            "a provenance record's FileName property, not a process launch"),
        ("tests/NVMeDriverPatcher.Tests/TelemetryReceiverSummaryTests.cs", "\"node\"",
            "node has no fixed install path and the test skips itself when it's missing"),
        ("tests/NVMeDriverPatcher.Tests/DataFileProvenanceServiceTests.cs", "\"compat.json\"",
            "a provenance record's FileName property, not a process launch"),
    ];

    private static IEnumerable<(string Target, int Index)> UntrustedLaunchTargets(string source) =>
        LaunchTarget.Matches(source)
            .Select(m => (Target: m.Groups["value"].Value.Trim(), m.Groups["value"].Index))
            .Where(hit => !TrustedLaunchTarget.IsMatch(hit.Target));

    [Fact]
    public void NoSourceStartsAProcessFromAnUnresolvedTarget()
    {
        // Self-check: bare literals with and without .exe, a FileName set in a separate statement, and
        // names passed through a variable all count...
        Assert.Single(UntrustedLaunchTargets("var psi = new ProcessStartInfo(\"sc.exe\");"));
        Assert.Single(UntrustedLaunchTargets("var psi = new ProcessStartInfo(\"schtasks\") { CreateNoWindow = true };"));
        Assert.Single(UntrustedLaunchTargets("var psi = new ProcessStartInfo { FileName = \"reg\", UseShellExecute = false };"));
        Assert.Single(UntrustedLaunchTargets("psi.FileName = \"dism\";"));
        Assert.Single(UntrustedLaunchTargets("var tool = \"bcdedit.exe\";\nvar psi = new ProcessStartInfo(tool, args);"));
        Assert.Single(UntrustedLaunchTargets("startInfo.FileName = toolPath;"));
        Assert.Single(UntrustedLaunchTargets("process.StartInfo = new System.Diagnostics.ProcessStartInfo(\n    \"cmd.exe\", args)"));
        Assert.Single(UntrustedLaunchTargets("Process.Start(\"explorer\");"));
        // ...and resolved launches, literal full paths and non-launch FileName shapes do not.
        Assert.Empty(UntrustedLaunchTargets("new ProcessStartInfo(SystemToolPathService.PowerShell)"));
        Assert.Empty(UntrustedLaunchTargets("new ProcessStartInfo(NVMeDriverPatcher.Services.SystemToolPathService.PowerShell)"));
        Assert.Empty(UntrustedLaunchTargets("new ProcessStartInfo(\n    SystemToolPathService.Resolve(\"cmd.exe\"), $\"/d /c {Path.Combine(dir, name)}\")"));
        Assert.Empty(UntrustedLaunchTargets("new ProcessStartInfo(SystemToolPathService.Resolve(Path.Combine(\"x\", \"y.exe\")))"));
        Assert.Empty(UntrustedLaunchTargets("new ProcessStartInfo(@\"C:\\Windows\\System32\\cmd.exe\")"));
        Assert.Empty(UntrustedLaunchTargets("var psi = new ProcessStartInfo();"));
        Assert.Empty(UntrustedLaunchTargets("public const string ManifestFileName = \"update-manifest.json\";"));
        Assert.Empty(UntrustedLaunchTargets("if (psi.FileName == expected) return;"));
        Assert.Empty(UntrustedLaunchTargets("using var proc = Process.Start(psi);"));

        // This file's string literals are the detectors' fixtures, not launches.
        var self = Path.Combine("tests", "NVMeDriverPatcher.Tests", nameof(SystemToolPathServiceTests) + ".cs");
        var hits = ShippedSourceFiles("src", "tests")
            .Where(path => !path.EndsWith(self, StringComparison.OrdinalIgnoreCase))
            .SelectMany(path =>
            {
                var relative = Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');
                // Blank out line comments so a doc example is not read as a launch.
                var source = Regex.Replace(File.ReadAllText(path), @"^[ \t]*//.*$", string.Empty, RegexOptions.Multiline);
                return UntrustedLaunchTargets(source).Select(hit =>
                    (File: relative, hit.Target, Line: source.AsSpan(0, hit.Index).Count('\n') + 1));
            })
            .ToList();

        var offenders = hits
            .Where(hit => !LaunchTargetAllowlist.Any(entry => entry.File == hit.File && entry.Target == hit.Target))
            .Select(hit => $"{hit.File}:{hit.Line} ({hit.Target})")
            .ToList();
        Assert.True(
            offenders.Count == 0,
            "These start a process from a target that isn't a SystemToolPathService call or a literal full path. " +
            $"Resolve it through SystemToolPathService, or add an allowlist entry that says where the value comes from: {string.Join(", ", offenders)}");

        var stale = LaunchTargetAllowlist
            .Where(entry => !hits.Any(hit => hit.File == entry.File && hit.Target == entry.Target))
            .Select(entry => $"{entry.File} ({entry.Target})")
            .ToList();
        Assert.True(stale.Count == 0, $"These allowlist entries no longer match a launch site; remove them: {string.Join(", ", stale)}");
    }

    [Fact]
    public void Launch_UsesTheSystem32PowerShellEvenWhenAPlantedOneSitsInTheWorkingDirectory()
    {
        var plantDir = Path.Combine(Path.GetTempPath(), "nvme-plant-ps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plantDir);
        try
        {
            // A stub that would be unmistakable if it ran: cmd.exe renamed to powershell.exe.
            File.Copy(SystemToolPathService.Resolve("cmd.exe"), Path.Combine(plantDir, "powershell.exe"));

            var psi = new System.Diagnostics.ProcessStartInfo(SystemToolPathService.PowerShell)
            {
                WorkingDirectory = plantDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Write-Output 'real-powershell'; exit 7" })
                psi.ArgumentList.Add(argument);

            var result = TestProcessRunner.Run(psi, TimeSpan.FromSeconds(30));

            Assert.False(result.TimedOut);
            Assert.Equal(7, result.ExitCode);
            Assert.Contains("real-powershell", result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(plantDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Resolve_IgnoresAToolPlantedInTheWorkingDirectory()
    {
        // The watchdog's control verbs only ever run elevated, so the concrete risk is a planted
        // sc.exe in the current directory being launched with a SYSTEM token. Prove the resolved
        // path is used by planting a stub that would be unmistakable if it ran.
        var plantDir = Path.Combine(Path.GetTempPath(), "nvme-plant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plantDir);
        try
        {
            var plant = Path.Combine(plantDir, "sc.exe");
            File.Copy(SystemToolPathService.Resolve("cmd.exe"), plant);

            var psi = new System.Diagnostics.ProcessStartInfo(SystemToolPathService.Resolve("sc.exe"))
            {
                WorkingDirectory = plantDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // A query for a service that does not exist: real sc.exe reports 1060, the planted
            // cmd.exe stub would sit waiting for input instead.
            psi.ArgumentList.Add("query");
            psi.ArgumentList.Add("NVMeDriverPatcherNoSuchService" + Guid.NewGuid().ToString("N"));

            using var proc = System.Diagnostics.Process.Start(psi)!;
            // Read asynchronously and bound the wait: a synchronous ReadToEnd turns a stub that
            // blocks on stdin into a wedged suite instead of one failing test.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                Assert.Fail("sc.exe did not exit — the planted stub may have run.");
            }

            Assert.Equal(1060, proc.ExitCode); // ERROR_SERVICE_DOES_NOT_EXIST
            Assert.Contains("1060", await stdout + await stderr, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(plantDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The PowerShell surface's version of the same defect: resolving a privileged executable
    /// through the current directory or <c>$PATH</c>. The module is written for an elevated
    /// session, so either lookup would run a planted binary with administrator rights.
    /// </summary>
    /// <remarks>
    /// Deliberately matches launch and lookup shapes rather than any <c>.exe</c> literal: the
    /// validation scripts carry allowlists of forbidden and required tool names, and flagging a
    /// name being *compared* would make the gate noisy enough to be disabled.
    /// </remarks>
    private const string KnownPowerShellTool =
        @"(?:dotnet(?:\.exe)?|powershell(?:\.exe)?|winget(?:\.exe)?|wix(?:\.exe)?|sc(?:\.exe)?|" +
        @"WindowsSandbox(?:\.exe)?|NVMeDriverPatcher\.Cli(?:\.exe)?)";

    private static readonly Regex PowerShellPathLookup = new(
        @"Get-Command\s+(-Name\s+)?['""]?" + KnownPowerShellTool + @"\b['""]?" +          // $PATH lookup
        @"|&\s*['""]?" + KnownPowerShellTool + @"\b['""]?" +                                      // & tool
        @"|Start-Process\s+(-FilePath\s+)?['""]?" + KnownPowerShellTool + @"\b['""]?" +       // Start-Process
        @"|Invoke-Checked\s+['""]?" + KnownPowerShellTool + @"\b['""]?",                         // wrapper parameter
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void NoShippedPackagingScriptResolvesAToolThroughPathOrTheCurrentDirectory()
    {
        // The src/-only scan never looked here, which is why the module shipped a $PATH fallback
        // for the requireAdministrator CLI exe.
        Assert.Matches(PowerShellPathLookup, "    $cmd = Get-Command -Name 'NVMeDriverPatcher.Cli.exe'");
        Assert.Matches(PowerShellPathLookup, "    $sandbox = Get-Command WindowsSandbox.exe");
        Assert.Matches(PowerShellPathLookup, "    & 'NVMeDriverPatcher.Cli.exe' status");
        Assert.Matches(PowerShellPathLookup, "    $output = & sc.exe $Command $serviceName"); // unquoted
        Assert.Matches(PowerShellPathLookup, "    Start-Process -FilePath 'sc.exe'");
        Assert.Matches(PowerShellPathLookup, "    Invoke-Checked powershell.exe @(");
        Assert.Matches(PowerShellPathLookup, "    Invoke-Checked winget.exe @(");
        Assert.Matches(PowerShellPathLookup, "    Invoke-Checked wix @(");
        Assert.Matches(PowerShellPathLookup, "    $output = & dotnet test project.csproj");
        // ...but stays quiet on a fully qualified launch and on a name merely being compared.
        Assert.DoesNotMatch(PowerShellPathLookup, "    (Join-Path $PSScriptRoot 'NVMeDriverPatcher.Cli.exe')");
        Assert.DoesNotMatch(PowerShellPathLookup, "    'pnputil.exe'");
        Assert.DoesNotMatch(PowerShellPathLookup, "    & $cli $Command @Arguments");
        Assert.DoesNotMatch(PowerShellPathLookup, "    $output = & $scExe $Command $serviceName");
        Assert.DoesNotMatch(PowerShellPathLookup, "    Invoke-Checked $powerShellPath @(");
        Assert.DoesNotMatch(PowerShellPathLookup, "    Invoke-Checked $dotnetPath @(");

        var offenders = new[] { "packaging", "scripts" }
            .Select(root => Path.Combine(RepoRoot(), root))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.ps*1", SearchOption.AllDirectories))
            .SelectMany(path => ExecutableScriptLines(File.ReadAllLines(path))
                .Where(entry => PowerShellPathLookup.IsMatch(entry.line))
                .Select(entry => $"{Path.GetFileName(path)}:{entry.number}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"These lines resolve an executable through $PATH or a relative candidate; use a fully qualified trusted path: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// Script lines the file itself executes, with comments and here-string bodies removed.
    /// </summary>
    /// <remarks>
    /// A here-string is a string literal, not code this script runs. If a here-string ever becomes
    /// a host-side payload, that is the point to revisit this.
    /// </remarks>
    private static IEnumerable<(string line, int number)> ExecutableScriptLines(string[] lines)
    {
        var inHereString = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (inHereString)
            {
                if (trimmed is "'@" or "\"@") inHereString = false;
                continue;
            }
            if (lines[i].EndsWith("@'", StringComparison.Ordinal) || lines[i].EndsWith("@\"", StringComparison.Ordinal))
            {
                inHereString = true;
                continue;
            }
            if (trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
            yield return (lines[i], i + 1);
        }
    }

    private static bool IsOffendingLine(string line)
    {
        var code = line.TrimStart();
        if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal))
            return false;
        return UnresolvedToolLiteral.Matches(line)
            .Concat(UnresolvedBareToolLiteral.Matches(line))
            .Any(m => !NonLaunchLiteralPrefix.IsMatch(line[..m.Index]));
    }

    private static IEnumerable<string> ShippedSourceFiles(params string[] relativeRoots) =>
        relativeRoots
            .Select(root => Path.Combine(RepoRoot(), root))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
}
