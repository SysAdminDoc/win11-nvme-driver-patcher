using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class RecoveryKitServiceTests : IDisposable
{
    /// <summary>
    /// No generated recovery artifact may delete a SafeBoot key outright. Windows ships these keys
    /// itself on 26200.8737+ with a NvmeDisk value, so removing the key takes the OS's own Safe
    /// Mode storage-disk registration with it and Safe Mode can stop seeing the boot disk — on a
    /// machine that is already being recovered (issue #13). Only the default value is ours, and it
    /// is what the residue probe checks, so clearing it removes all of ours and none of Windows'.
    /// </summary>
    [Fact]
    public void GeneratedArtifacts_NeverDeleteAnEntireSafeBootKey()
    {
        var reg = RecoveryKitService.BuildRegContent("001", "2026-08-11T00:00:00Z");
        var bat = RecoveryKitService.BuildBatContent();

        foreach (var leaf in new[] { AppConfig.SafeBootGuid, AppConfig.SafeBootServiceName })
            foreach (var store in new[] { "Minimal", "Network" })
            {
                Assert.DoesNotContain($@"[-HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SafeBoot\{store}\{leaf}]", reg);
                Assert.DoesNotContain($@"\Control\SafeBoot\{store}\{leaf}"" /f ", bat);
            }

        // The default-value deletes must still be there — this must not pass by emitting nothing.
        Assert.Contains("@=-", reg);
        Assert.Contains(@"\Control\SafeBoot\Minimal\" + AppConfig.SafeBootGuid + @""" /ve /f", bat);
    }

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.RecoveryKit.Tests.{Guid.NewGuid():N}");

    public RecoveryKitServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void GenerateVerificationScript_SafeProfileChecksOnlySafeFeatureSet()
    {
        var path = RecoveryKitService.GenerateVerificationScript(_tempRoot, PatchProfile.Safe, includeServerKey: false);

        Assert.NotNull(path);
        var script = File.ReadAllText(path!);
        Assert.Contains("Expected profile: Safe", script, StringComparison.Ordinal);
        Assert.Contains("735209102", script, StringComparison.Ordinal);
        Assert.DoesNotContain("1853569164", script, StringComparison.Ordinal);
        Assert.DoesNotContain("156965516", script, StringComparison.Ordinal);
        Assert.DoesNotContain("1176759950", script, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateVerificationScript_FullProfileIncludesExtendedAndServerKeysWhenRequested()
    {
        var path = RecoveryKitService.GenerateVerificationScript(_tempRoot, PatchProfile.Full, includeServerKey: true);

        Assert.NotNull(path);
        var script = File.ReadAllText(path!);
        Assert.Contains("Expected profile: Full", script, StringComparison.Ordinal);
        Assert.Contains("735209102", script, StringComparison.Ordinal);
        Assert.Contains("1853569164", script, StringComparison.Ordinal);
        Assert.Contains("156965516", script, StringComparison.Ordinal);
        Assert.Contains("1176759950", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportRecoveryKit_BatchUsesWinPeDetectionForOfflineRecovery()
    {
        var kitDir = RecoveryKitService.Export(_tempRoot);

        Assert.NotNull(kitDir);
        var batch = File.ReadAllText(Path.Combine(kitDir!, RecoveryKitService.MutationScriptFileName));
        Assert.Contains(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\WinPE", batch, StringComparison.Ordinal);
        Assert.DoesNotContain(@"reg query ""HKLM\SYSTEM\CurrentControlSet""", batch, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("for /L %%N in (1,1,9)", batch, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportRecoveryKit_ReadmeDocumentsRegistryOwnershipRecovery()
    {
        var kitDir = RecoveryKitService.Export(_tempRoot);

        Assert.NotNull(kitDir);
        var readme = File.ReadAllText(Path.Combine(kitDir!, "README.txt"));
        Assert.Contains("REGISTRY OWNERSHIP RESIDUE", readme, StringComparison.Ordinal);
        Assert.Contains("TrustedInstaller", readme, StringComparison.Ordinal);
        Assert.Contains("takeown /f", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reg delete", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AppConfig.RegistryPath, readme, StringComparison.Ordinal);
        Assert.Contains("Do not delete the entire Overrides key", readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_RemovalArtifacts_CoverServiceNameSafeBootEntries()
    {
        // The kit must remove BOTH SafeBoot entry styles: the GUID-class entries and the
        // KB5079391-era service-name entries (added v4.6.1) — a kit that leaves
        // SafeBoot\*\nvmedisk behind doesn't fully revert the patch.
        var kitDir = RecoveryKitService.Export(_tempRoot);
        Assert.NotNull(kitDir);

        var reg = File.ReadAllText(Path.Combine(kitDir!, "NVMe_Remove_Patch.reg"));
        Assert.Contains(@"SafeBoot\Minimal\nvmedisk", reg);
        Assert.Contains(@"SafeBoot\Network\nvmedisk", reg);
        Assert.Contains("Remove_NVMe_Patch.bat is the canonical removal path", reg);

        var bat = File.ReadAllText(Path.Combine(kitDir!, RecoveryKitService.MutationScriptFileName));
        Assert.Contains(@"SafeBoot\Minimal\nvmedisk", bat);
        Assert.Contains(@"SafeBoot\Network\nvmedisk", bat);
        // Offline sweep covers rolled control sets; service-name entries must be in the loop.
        Assert.Contains(@"ControlSet00%%N\Control\SafeBoot\Minimal\nvmedisk", bat);
    }

    [Fact]
    public void BuildRegContent_DerivesIdsAndKeysFromAppConfig()
    {
        var reg = RecoveryKitService.BuildRegContent("003", "2026-06-14 12:00:00");

        // Every patch feature ID + the optional Server key, sourced from AppConfig — so a future
        // ID change flows through instead of leaving the kit deleting stale values.
        foreach (var id in AppConfig.FeatureIDs)
            Assert.Contains($"\"{id}\"=-", reg, StringComparison.Ordinal);
        Assert.Contains($"\"{AppConfig.ServerFeatureID}\"=-", reg, StringComparison.Ordinal);

        Assert.Contains(AppConfig.SafeBootGuid, reg, StringComparison.Ordinal);
        Assert.Contains($@"SafeBoot\Minimal\{AppConfig.SafeBootServiceName}", reg, StringComparison.Ordinal);

        // Value-deletes appear once per control set (CurrentControlSet + ControlSet003).
        int expected = (AppConfig.FeatureIDs.Count + 1) * 2;
        int actual = reg.Split("\"=-").Length - 1;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildBatContent_DerivesIdsFromAppConfig_AndIsUniformCrlf()
    {
        var bat = RecoveryKitService.BuildBatContent();

        foreach (var id in AppConfig.FeatureIDs)
            Assert.Contains($"/v {id} /f", bat, StringComparison.Ordinal);
        Assert.Contains($"/v {AppConfig.ServerFeatureID} /f", bat, StringComparison.Ordinal);
        Assert.Contains($@"SafeBoot\Network\{AppConfig.SafeBootServiceName}", bat, StringComparison.Ordinal);
        Assert.Contains(AppConfig.SafeBootGuid, bat, StringComparison.Ordinal);

        // No stray LF once CRLF pairs are removed — guards against mixed line endings.
        Assert.DoesNotContain("\n", bat.Replace("\r\n", string.Empty));
    }

    [Fact]
    public void Export_PublishesVerifiedManifestAndGuardsMutationBehindHashChecks()
    {
        var kitDir = RecoveryKitService.Export(_tempRoot);

        Assert.NotNull(kitDir);
        var manifestPath = Path.Combine(kitDir!, GeneratedArtifactManifestService.ManifestFileName);
        Assert.True(File.Exists(manifestPath));
        var verification = GeneratedArtifactManifestService.VerifyDirectory(kitDir);
        Assert.True(verification.Success, verification.Summary);
        Assert.Equal("recovery-kit", verification.PayloadType);

        var guard = File.ReadAllText(Path.Combine(kitDir, RecoveryKitService.GuardScriptFileName));
        // Availability is probed against the absolute System32 path, not via PATH lookup.
        Assert.Contains(@"if not exist ""%SystemRoot%\System32\certutil.exe""", guard, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Expected exactly 5 recovery-kit files", guard, StringComparison.Ordinal);
        Assert.Contains("failed SHA-256 verification", guard, StringComparison.Ordinal);
        Assert.Contains($"call \"{RecoveryKitService.MutationScriptFileName}\"", guard, StringComparison.Ordinal);
        Assert.True(guard.IndexOf($"call :verify \"{RecoveryKitService.MutationScriptFileName}\"", StringComparison.Ordinal) <
                    guard.IndexOf($"call \"{RecoveryKitService.MutationScriptFileName}\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Export_TamperedMutationIsReportedAndGuardPinsOriginalHash()
    {
        var kitDir = RecoveryKitService.Export(_tempRoot);
        Assert.NotNull(kitDir);
        var mutationPath = Path.Combine(kitDir!, RecoveryKitService.MutationScriptFileName);
        var originalHash = GeneratedArtifactManifestService.ComputeSha256(mutationPath);
        var guard = File.ReadAllText(Path.Combine(kitDir, RecoveryKitService.GuardScriptFileName));
        Assert.Contains(originalHash, guard, StringComparison.OrdinalIgnoreCase);

        File.AppendAllText(mutationPath, "\r\nrem corrupted");

        var verification = GeneratedArtifactManifestService.VerifyDirectory(kitDir);
        Assert.False(verification.Success);
        Assert.Contains(verification.Issues, issue =>
            issue.RelativePath == RecoveryKitService.MutationScriptFileName &&
            issue.Kind == ArtifactIntegrityIssueKind.LengthMismatch);
    }

    [Fact]
    public async Task GuardScript_RefusesSameLengthTamperBeforeMutationScriptRuns()
    {
        var kitDir = RecoveryKitService.Export(_tempRoot);
        Assert.NotNull(kitDir);
        var mutationPath = Path.Combine(kitDir!, RecoveryKitService.MutationScriptFileName);
        var bytes = File.ReadAllBytes(mutationPath);
        bytes[0] ^= 0x01; // preserve length so the SHA-256 check, not the length check, must stop it
        File.WriteAllBytes(mutationPath, bytes);

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/d /c \"\"{Path.Combine(kitDir, RecoveryKitService.GuardScriptFileName)}\"\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = kitDir
        };
        process.Start();

        // Read asynchronously and bound the wait. A synchronous ReadToEnd() here blocks until the
        // child closes stdout, so a guard script that never exits wedges the whole test run instead
        // of failing this one test — that is exactly how the PATH-shadowed `find` hang presented.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Recovery integrity guard did not exit.");
        }

        var output = await stdout + await stderr;

        Assert.Equal(2, process.ExitCode);
        Assert.Contains("failed SHA-256 verification", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Patch removed. Reboot", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedScripts_ResolveExternalToolsByAbsolutePath()
    {
        // A bare `find`/`reg`/`certutil` resolves through PATH. Git for Windows ships a GNU
        // find.exe in usr\bin, so `dir | find /c /v ""` handed the count to GNU find, which read
        // "/c" as a directory and walked the whole drive — the integrity gate never returned.
        // The kit is also the elevated last-resort recovery path, so a PATH-resolved tool is a
        // binary-planting vector. Every external tool must be invoked by absolute path.
        var bareToolAtCommandPosition = new System.Text.RegularExpressions.Regex(
            @"(?:^|[|(]|\bdo\s+|\bin\s+\(')\s*(reg|certutil|find|where|sort|findstr)(?:\.exe)?\s",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Multiline);

        // Self-check: the detector must actually fire on the shape of the original defect,
        // otherwise a green result below would prove nothing.
        Assert.Matches(bareToolAtCommandPosition, @"for /f %%C in ('dir /b /s /a-d 2>nul | find /c /v """") do set X=%%C");
        Assert.Matches(bareToolAtCommandPosition, @"reg delete ""HKLM\SYSTEM\CurrentControlSet"" /f");

        var kitDir = RecoveryKitService.Export(_tempRoot);
        Assert.NotNull(kitDir);

        foreach (var scriptName in new[] { RecoveryKitService.GuardScriptFileName, RecoveryKitService.MutationScriptFileName })
        {
            var script = File.ReadAllText(Path.Combine(kitDir!, scriptName));

            // An un-interpolated placeholder emits a literal "{Sys32}\reg.exe", which is not a
            // runnable path — the offline hive would silently never unload.
            Assert.DoesNotContain("{Sys32}", script, StringComparison.Ordinal);
            Assert.Contains(@"%SystemRoot%\System32", script, StringComparison.OrdinalIgnoreCase);

            var executable = string.Join(
                '\n',
                script.Split('\n')
                    .Select(line => line.Trim())
                    .Where(line => !line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase))
                    .Where(line => !line.StartsWith("echo ", StringComparison.OrdinalIgnoreCase))
                    .Where(line => !line.StartsWith(';'))
                    // Absolute-path invocations are the compliant form; drop them before scanning
                    // so only a genuinely bare tool name can trip the detector.
                    .Select(line => line.Replace(@"%SystemRoot%\System32\", "ABSOLUTE_", StringComparison.OrdinalIgnoreCase)));

            Assert.DoesNotMatch(bareToolAtCommandPosition, executable);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup only.
        }
    }
}
