using System.Diagnostics;
using System.IO;
using System.Text;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

internal delegate Task WinPECommandRunner(
    string file,
    string[] args,
    int timeoutSeconds,
    CancellationToken cancellationToken);

public class WinPEBuildOptions
{
    /// <summary>
    /// Target output folder — a full bootable WinPE tree will be built here. Pass a USB root
    /// (e.g. "E:\") to produce a ready-to-boot stick, or a local folder to preview.
    /// </summary>
    public string OutputDir { get; set; } = string.Empty;

    /// <summary>Path to the existing Recovery Kit folder that will be copied into WinPE.</summary>
    public string RecoveryKitDir { get; set; } = string.Empty;

    /// <summary>Produce an .iso alongside the tree (useful for VM testing / burning).</summary>
    public bool ProduceIso { get; set; } = true;

    /// <summary>x64 only — we don't support arm64 WinPE targets in v4.4.</summary>
    public string Architecture { get; set; } = "amd64";
}

public class WinPEBuildResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? IsoPath { get; set; }
    public string? WimPath { get; set; }
    public string? TreeDir { get; set; }
    public string? MediaRoot { get; set; }
    public string? ControllerReportPath { get; set; }
    public List<BootStorageController> Controllers { get; set; } = [];
    public List<string> Warnings { get; set; } = new();
}

// Builds a bootable WinPE recovery stick with the project's Recovery Kit pre-loaded.
// Wraps the Windows ADK's copype / MakeWinPEMedia / Dism tools — the user must have the
// ADK + WinPE add-on installed (we detect and prompt with a direct download link).
//
// This is the "real fallback confidence" closeout from ROADMAP.md §3.3 — the Recovery Kit
// today assumes the user can still boot. A WinPE stick closes the case where they can't.
public static class WinPERecoveryBuilderService
{
    internal static readonly string[] AdkWinPERoots =
    {
        @"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Windows Preinstallation Environment",
        @"C:\Program Files\Windows Kits\10\Assessment and Deployment Kit\Windows Preinstallation Environment"
    };

    public const string AdkDownloadUrl = "https://learn.microsoft.com/windows-hardware/get-started/adk-install";

    public static bool IsAdkAvailable(out string? winPeRoot)
    {
        winPeRoot = null;
        foreach (var candidate in AdkWinPERoots)
        {
            try
            {
                if (Directory.Exists(candidate))
                {
                    winPeRoot = candidate;
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    public static async Task<WinPEBuildResult> BuildAsync(
        WinPEBuildOptions options,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var result = new WinPEBuildResult();

        if (string.IsNullOrWhiteSpace(options.OutputDir))
        {
            result.Success = false;
            result.Summary = "OutputDir not set.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(options.RecoveryKitDir) || !Directory.Exists(options.RecoveryKitDir))
        {
            result.Success = false;
            result.Summary = "A generated, self-verifying Recovery Kit is required before WinPE media can be built.";
            return result;
        }

        var kitIntegrity = GeneratedArtifactManifestService.VerifyDirectory(options.RecoveryKitDir);
        if (!kitIntegrity.Success)
        {
            result.Success = false;
            result.Summary = "Recovery Kit integrity failed: " +
                string.Join("; ", kitIntegrity.Issues.Select(i => $"{i.RelativePath}: {i.Detail}"));
            return result;
        }

        var controllerInventory = BootStorageControllerService.Inventory();
        result.Controllers = controllerInventory.Controllers;
        if (!controllerInventory.ProbeSucceeded || controllerInventory.Controllers.Count == 0 ||
            controllerInventory.Controllers.Any(c => c.Coverage == WinPEControllerCoverage.Missing))
        {
            result.Success = false;
            result.Summary = controllerInventory.Summary;
            return result;
        }

        if (!IsAdkAvailable(out var winPeRoot))
        {
            result.Success = false;
            result.Summary = "Windows ADK + WinPE add-on not detected. Install from: " + AdkDownloadUrl;
            return result;
        }

        WinPESourceSnapshot sourceSnapshot;
        try
        {
            sourceSnapshot = await WinPEMediaFreshnessService.CaptureCurrentSourcesAsync(
                options.RecoveryKitDir,
                controllerInventory,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Summary = $"Could not fingerprint recovery-media sources: {ex.Message}";
            return result;
        }

        string? stagingTreeDir = null;
        string? temporaryIsoPath = null;
        try
        {
            Directory.CreateDirectory(options.OutputDir);
            var finalTreeDir = Path.Combine(options.OutputDir, "NVMePatcher_WinPE");
            stagingTreeDir = Path.Combine(options.OutputDir, $"NVMePatcher_WinPE.{Guid.NewGuid():N}.tmp");
            var finalIsoPath = options.ProduceIso
                ? Path.Combine(options.OutputDir, "NVMePatcher_Recovery.iso")
                : null;
            temporaryIsoPath = options.ProduceIso
                ? Path.Combine(options.OutputDir, $"NVMePatcher_Recovery.{Guid.NewGuid():N}.tmp.iso")
                : null;
            result.TreeDir = finalTreeDir;
            result.MediaRoot = Path.Combine(finalTreeDir, "media");
            result.WimPath = Path.Combine(finalTreeDir, "media", "sources", "boot.wim");

            var copypeCmd = Path.Combine(winPeRoot!, "copype.cmd");
            if (!File.Exists(copypeCmd))
            {
                result.Success = false;
                result.Summary = $"copype.cmd missing — expected at {copypeCmd}";
                return result;
            }

            log?.Invoke("[INFO] Running copype.cmd...");
            await RunProcessAsync(
                "cmd.exe",
                new[] { "/c", copypeCmd, options.Architecture, stagingTreeDir },
                timeoutSeconds: 120,
                cancellationToken: cancellationToken);

            // Copy the Recovery Kit onto the media so it's reachable as a data folder from WinPE.
            if (!string.IsNullOrWhiteSpace(options.RecoveryKitDir) && Directory.Exists(options.RecoveryKitDir))
            {
                var kitOut = Path.Combine(stagingTreeDir, "media", "NVMe_Recovery_Kit");
                try
                {
                    var sourceIntegrity = GeneratedArtifactManifestService.VerifyDirectory(options.RecoveryKitDir);
                    if (!sourceIntegrity.Success)
                        throw new InvalidDataException("Source Recovery Kit failed integrity verification: " +
                            string.Join("; ", sourceIntegrity.Issues.Select(i => $"{i.RelativePath}: {i.Detail}")));
                    CopyDir(options.RecoveryKitDir, kitOut);
                    var copiedIntegrity = GeneratedArtifactManifestService.VerifyDirectory(kitOut);
                    if (!copiedIntegrity.Success)
                        throw new InvalidDataException("Copied Recovery Kit failed integrity verification: " +
                            string.Join("; ", copiedIntegrity.Issues.Select(i => $"{i.RelativePath}: {i.Detail}")));
                    log?.Invoke($"[INFO] Recovery Kit copied to {kitOut}");
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"Recovery Kit copy failed: {ex.Message}", ex);
                }
            }

            var mediaRoot = Path.Combine(stagingTreeDir, "media");
            await ExportControllerPackagesAsync(
                mediaRoot,
                controllerInventory,
                RunProcessAsync,
                log,
                cancellationToken).ConfigureAwait(false);
            if (controllerInventory.Controllers.Any(c => c.Coverage == WinPEControllerCoverage.Missing))
                throw new InvalidDataException("Controller package export incomplete: " +
                    string.Join("; ", controllerInventory.Controllers
                        .Where(c => c.Coverage == WinPEControllerCoverage.Missing)
                        .Select(c => $"{c.FriendlyName}: {c.Detail}")));

            // Mount once: inject every exported OEM controller INF and startnet.cmd, then commit
            // only when every controller has a truthful Inbox/Injected result.
            await CustomizeBootWimAsync(
                stagingTreeDir,
                controllerInventory,
                RunProcessAsync,
                log,
                cancellationToken).ConfigureAwait(false);

            var controllerReport = WinPEMediaFreshnessService.CreateBuildReport(
                sourceSnapshot,
                controllerInventory.Controllers);
            _ = WinPEMediaFreshnessService.PublishBuildReport(
                mediaRoot,
                controllerReport);

            // The media root is the payload users copy to USB and the exact tree consumed by
            // MakeWinPEMedia. Publish its inventory only after boot.wim injection is committed.
            var mediaIntegrity = PublishMediaManifest(mediaRoot);
            if (!mediaIntegrity.Success)
                throw new InvalidDataException(mediaIntegrity.Summary + " " +
                    string.Join("; ", mediaIntegrity.Issues.Select(i => $"{i.RelativePath}: {i.Detail}")));
            log?.Invoke($"[INFO] WinPE media manifest published at {Path.Combine(mediaRoot, GeneratedArtifactManifestService.ManifestFileName)}");

            if (options.ProduceIso)
            {
                var makeMedia = Path.Combine(winPeRoot!, "MakeWinPEMedia.cmd");
                log?.Invoke("[INFO] Producing ISO via MakeWinPEMedia /ISO...");
                await RunProcessAsync(
                    "cmd.exe",
                    new[] { "/c", makeMedia, "/ISO", stagingTreeDir, temporaryIsoPath! },
                    timeoutSeconds: 300,
                    cancellationToken: cancellationToken);
                if (File.Exists(temporaryIsoPath))
                {
                    result.IsoPath = finalIsoPath;
                }
                else
                {
                    throw new InvalidDataException("MakeWinPEMedia returned 0 but the temporary ISO is missing.");
                }
            }

            var finalIntegrity = GeneratedArtifactManifestService.VerifyDirectory(mediaRoot);
            if (!finalIntegrity.Success)
                throw new InvalidDataException("WinPE media changed after manifest publication: " +
                    string.Join("; ", finalIntegrity.Issues.Select(i => $"{i.RelativePath}: {i.Detail}")));

            PromoteBuildOutputs(stagingTreeDir, finalTreeDir, temporaryIsoPath, finalIsoPath);
            stagingTreeDir = null;
            temporaryIsoPath = null;
            result.ControllerReportPath = Path.Combine(
                result.MediaRoot,
                WinPEMediaFreshnessService.ControllerDirectoryName,
                WinPEMediaFreshnessService.ReportFileName);
            if (result.IsoPath is not null)
                log?.Invoke($"[SUCCESS] ISO written to {result.IsoPath}");

            result.Success = true;
            result.Summary = result.IsoPath is not null
                ? $"WinPE media built. ISO: {result.IsoPath}"
                : $"WinPE tree built at {result.TreeDir}";
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Summary = "WinPE build canceled.";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Summary = $"WinPE build failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            try { if (!string.IsNullOrWhiteSpace(stagingTreeDir) && Directory.Exists(stagingTreeDir)) Directory.Delete(stagingTreeDir, recursive: true); } catch { }
            try { if (!string.IsNullOrWhiteSpace(temporaryIsoPath) && File.Exists(temporaryIsoPath)) File.Delete(temporaryIsoPath); } catch { }
        }
        return result;
    }

    // Path of startnet.cmd INSIDE the mounted boot.wim — \Windows\System32\startnet.cmd is the
    // ONLY location WinPE executes at boot (NOT the media's \sources folder).
    internal static string StartnetTargetPath(string mountDir) =>
        Path.Combine(mountDir, "Windows", "System32", "startnet.cmd");

    internal static string BuildStartnetContent()
    {
        var content = new StringBuilder();
        content.AppendLine("@echo off");
        content.AppendLine("wpeinit");
        content.AppendLine();
        content.AppendLine("echo ====================================================");
        content.AppendLine("echo  NVMe Driver Patcher - WinPE Recovery");
        content.AppendLine("echo ====================================================");
        content.AppendLine("echo.");
        content.AppendLine("echo The Recovery Kit is on this boot media (folder NVMe_Recovery_Kit).");
        content.AppendLine("echo To remove the patch, find the media drive (try D:, E:, F:) and run:");
        content.AppendLine("echo   <drive>:");
        content.AppendLine("echo   cd \\NVMe_Recovery_Kit");
        content.AppendLine("echo   Remove_NVMe_Patch.bat");
        content.AppendLine("echo.");
        content.AppendLine("echo Controller coverage is recorded in \\NVMe_Controller_Drivers\\CONTROLLER-REPORT.json.");
        content.AppendLine("echo If a disk is still missing, run \\NVMe_Controller_Drivers\\Load_Controller_Drivers.bat");
        content.AppendLine("echo from this boot media, then use diskpart rescan.");
        content.AppendLine("echo.");
        return content.ToString();
    }

    // Writes startnet.cmd into a mounted image tree (pure filesystem — the testable seam).
    internal static void WriteStartnetToMount(string mountDir)
    {
        var target = StartnetTargetPath(mountDir);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, BuildStartnetContent(), Encoding.ASCII);
    }

    internal static async Task ExportControllerPackagesAsync(
        string mediaRoot,
        BootStorageControllerInventory inventory,
        WinPECommandRunner runner,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var driverRoot = Path.Combine(mediaRoot, WinPEMediaFreshnessService.ControllerDirectoryName);
        var packagesRoot = Path.Combine(driverRoot, "Packages");
        Directory.CreateDirectory(packagesRoot);
        File.WriteAllText(
            Path.Combine(driverRoot, "Load_Controller_Drivers.bat"),
            BuildManualDrvloadScript(),
            Encoding.ASCII);

        foreach (var packageGroup in inventory.Controllers
                     .Where(c => c.Coverage == WinPEControllerCoverage.PendingInjection)
                     .GroupBy(c => c.InfName, StringComparer.OrdinalIgnoreCase))
        {
            var infName = packageGroup.Key;
            if (!BootStorageControllerService.IsOemInf(infName))
            {
                MarkPackageGroup(packageGroup, WinPEControllerCoverage.Missing,
                    $"Bound INF '{infName}' is not a valid OEM package name.");
                continue;
            }

            var packageDir = Path.Combine(packagesRoot, Path.GetFileNameWithoutExtension(infName).ToLowerInvariant());
            try
            {
                if (Directory.Exists(packageDir)) Directory.Delete(packageDir, recursive: true);
                Directory.CreateDirectory(packageDir);
                log?.Invoke($"[INFO] Exporting bound controller package {infName}...");
                await runner(
                    "pnputil.exe",
                    ["/export-driver", infName, packageDir],
                    120,
                    cancellationToken).ConfigureAwait(false);

                var exportedInfs = Directory.EnumerateFiles(packageDir, "*.inf", SearchOption.AllDirectories)
                    .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (exportedInfs.Length != 1)
                    throw new InvalidDataException(
                        $"PnPUtil exported {exportedInfs.Length} INF files; exactly one package INF was expected.");

                var relativeInf = Path.GetRelativePath(mediaRoot, exportedInfs[0]).Replace('\\', '/');
                var hash = GeneratedArtifactManifestService.ComputeSha256(exportedInfs[0]);
                foreach (var controller in packageGroup)
                {
                    controller.PackageRelativeInfPath = relativeInf;
                    controller.PackageSha256 = hash;
                    controller.Detail = $"Exported {infName} for signed DISM injection.";
                }
            }
            catch (Exception ex)
            {
                MarkPackageGroup(packageGroup, WinPEControllerCoverage.Missing,
                    $"Could not export {infName}: {ex.Message}");
                try { if (Directory.Exists(packageDir)) Directory.Delete(packageDir, recursive: true); } catch { }
            }
        }
    }

    internal static async Task CustomizeBootWimAsync(
        string treeDir,
        BootStorageControllerInventory inventory,
        WinPECommandRunner runner,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var bootWim = Path.Combine(treeDir, "media", "sources", "boot.wim");
        var mediaRoot = Path.Combine(treeDir, "media");
        var mountDir = Path.Combine(treeDir, "mount");
        if (!File.Exists(bootWim))
            throw new FileNotFoundException("copype did not produce media\\sources\\boot.wim.", bootWim);

        bool mounted = false;
        try
        {
            Directory.CreateDirectory(mountDir);
            log?.Invoke("[INFO] Mounting boot.wim for controller and startnet injection...");
            await runner("dism.exe",
                ["/Mount-Image", $"/ImageFile:{bootWim}", "/Index:1", $"/MountDir:{mountDir}"],
                300, cancellationToken).ConfigureAwait(false);
            mounted = true;

            WriteStartnetToMount(mountDir);
            log?.Invoke(@"[INFO] startnet.cmd injected into boot.wim\Windows\System32.");

            foreach (var packageGroup in inventory.Controllers
                         .Where(c => c.Coverage == WinPEControllerCoverage.PendingInjection)
                         .GroupBy(c => c.PackageRelativeInfPath, StringComparer.OrdinalIgnoreCase))
            {
                var relativeInf = packageGroup.Key;
                if (string.IsNullOrWhiteSpace(relativeInf))
                {
                    MarkPackageGroup(packageGroup, WinPEControllerCoverage.Missing,
                        "No exported INF path is available for DISM injection.");
                    continue;
                }

                string infPath;
                try
                {
                    infPath = Path.GetFullPath(Path.Combine(
                        mediaRoot,
                        relativeInf.Replace('/', Path.DirectorySeparatorChar)));
                    var relativeToMedia = Path.GetRelativePath(mediaRoot, infPath);
                    if (Path.IsPathRooted(relativeToMedia) ||
                        relativeToMedia.Equals("..", StringComparison.Ordinal) ||
                        relativeToMedia.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                        throw new InvalidDataException("Exported INF path escapes the WinPE media root.");
                    if (!File.Exists(infPath))
                        throw new FileNotFoundException("Exported controller INF is missing.", infPath);
                    var expectedHash = packageGroup.Select(c => c.PackageSha256)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .SingleOrDefault();
                    if (string.IsNullOrWhiteSpace(expectedHash))
                        throw new InvalidDataException("Exported controller INF has no recorded SHA-256.");
                    var actualHash = GeneratedArtifactManifestService.ComputeSha256(infPath);
                    if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            $"Exported controller INF hash changed (expected {expectedHash}; found {actualHash}).");

                    log?.Invoke($"[INFO] Injecting controller INF {relativeInf} into boot.wim...");
                    await runner("dism.exe",
                        [$"/Image:{mountDir}", "/Add-Driver", $"/Driver:{infPath}"],
                        300, cancellationToken).ConfigureAwait(false);
                    MarkPackageGroup(packageGroup, WinPEControllerCoverage.Injected,
                        $"Injected {relativeInf} into boot.wim; package retained for manual drvload.");
                }
                catch (Exception ex)
                {
                    MarkPackageGroup(packageGroup, WinPEControllerCoverage.Missing,
                        $"DISM rejected {relativeInf}: {ex.Message}");
                }
            }

            var missing = inventory.Controllers
                .Where(c => c.Coverage is WinPEControllerCoverage.Missing or WinPEControllerCoverage.PendingInjection)
                .ToArray();
            if (missing.Length > 0)
                throw new InvalidDataException("boot.wim controller coverage incomplete: " +
                    string.Join("; ", missing.Select(c => $"{c.FriendlyName}: {c.Detail}")));

            await runner("dism.exe",
                ["/Unmount-Image", $"/MountDir:{mountDir}", "/Commit"],
                300, cancellationToken).ConfigureAwait(false);
            mounted = false;
            log?.Invoke("[INFO] boot.wim controller and startnet changes committed.");
        }
        catch
        {
            if (mounted)
            {
                try
                {
                    await runner("dism.exe",
                        ["/Unmount-Image", $"/MountDir:{mountDir}", "/Discard"],
                        180, CancellationToken.None).ConfigureAwait(false);
                    mounted = false;
                }
                catch (Exception discardEx)
                {
                    log?.Invoke($"[WARNING] DISM discard failed: {discardEx.Message}. Run 'Dism /Cleanup-Mountpoints'.");
                }
            }
            throw;
        }
        finally
        {
            try { if (Directory.Exists(mountDir)) Directory.Delete(mountDir, recursive: true); } catch { }
        }
    }

    internal static string BuildManualDrvloadScript()
    {
        const string content = """
@echo off
setlocal EnableExtensions EnableDelayedExpansion
set "FOUND=0"
set "FAILED=0"
for /r "%~dp0Packages" %%I in (*.inf) do (
    set "FOUND=1"
    echo Loading %%I
    drvload.exe "%%I"
    if errorlevel 1 set "FAILED=1"
)
if "!FOUND!"=="0" (
    echo ERROR: No exported controller INF packages were found.
    exit /b 2
)
if "!FAILED!"=="1" (
    echo ERROR: One or more controller packages failed to load.
    exit /b 1
)
echo Controller packages loaded. Open diskpart and type: rescan
exit /b 0
""";
        return content.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    private static void MarkPackageGroup(
        IEnumerable<BootStorageController> controllers,
        WinPEControllerCoverage coverage,
        string detail)
    {
        foreach (var controller in controllers)
        {
            controller.Coverage = coverage;
            controller.Detail = detail;
        }
    }

    internal static void PromoteBuildOutputs(
        string stagingTreeDir,
        string finalTreeDir,
        string? temporaryIsoPath,
        string? finalIsoPath)
    {
        var stagingTree = Path.GetFullPath(stagingTreeDir);
        var finalTree = Path.GetFullPath(finalTreeDir);
        var outputRoot = Path.GetDirectoryName(finalTree)
                         ?? throw new InvalidDataException("WinPE output root is unavailable.");
        if (!Directory.Exists(stagingTree))
            throw new DirectoryNotFoundException($"Staged WinPE tree is missing: {stagingTree}");
        if (!string.Equals(Path.GetDirectoryName(stagingTree), outputRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Staged and final WinPE trees must share one output root.");

        string? tempIso = null;
        string? finalIso = null;
        if (temporaryIsoPath is not null || finalIsoPath is not null)
        {
            if (string.IsNullOrWhiteSpace(temporaryIsoPath) || string.IsNullOrWhiteSpace(finalIsoPath))
                throw new InvalidDataException("Temporary and final ISO paths must be supplied together.");
            tempIso = Path.GetFullPath(temporaryIsoPath);
            finalIso = Path.GetFullPath(finalIsoPath);
            if (!File.Exists(tempIso)) throw new FileNotFoundException("Staged WinPE ISO is missing.", tempIso);
            if (!string.Equals(Path.GetDirectoryName(tempIso), outputRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(finalIso), outputRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staged and final WinPE ISO files must share the tree output root.");
        }

        var token = Guid.NewGuid().ToString("N");
        var treeBackup = finalTree + $".{token}.bak";
        var isoBackup = finalIso is null ? null : finalIso + $".{token}.bak";
        bool oldTreeMoved = false;
        bool newTreePromoted = false;
        bool oldIsoMoved = false;
        bool newIsoPromoted = false;
        bool promotionSucceeded = false;

        try
        {
            if (Directory.Exists(finalTree))
            {
                Directory.Move(finalTree, treeBackup);
                oldTreeMoved = true;
            }
            Directory.Move(stagingTree, finalTree);
            newTreePromoted = true;

            if (tempIso is not null && finalIso is not null)
            {
                if (File.Exists(finalIso))
                {
                    File.Move(finalIso, isoBackup!);
                    oldIsoMoved = true;
                }
                File.Move(tempIso, finalIso);
                newIsoPromoted = true;
            }
            promotionSucceeded = true;
        }
        catch
        {
            try { if (newIsoPromoted && finalIso is not null && File.Exists(finalIso)) File.Delete(finalIso); } catch { }
            try { if (oldIsoMoved && isoBackup is not null && finalIso is not null && File.Exists(isoBackup)) File.Move(isoBackup, finalIso); } catch { }
            try { if (newTreePromoted && Directory.Exists(finalTree)) Directory.Delete(finalTree, recursive: true); } catch { }
            try { if (oldTreeMoved && Directory.Exists(treeBackup)) Directory.Move(treeBackup, finalTree); } catch { }
            throw;
        }
        finally
        {
            if (promotionSucceeded)
            {
                try { if (Directory.Exists(treeBackup)) Directory.Delete(treeBackup, recursive: true); } catch { }
                try { if (isoBackup is not null && File.Exists(isoBackup)) File.Delete(isoBackup); } catch { }
            }
        }
    }

    internal static void CopyDir(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var dst = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    internal static ArtifactIntegrityResult PublishMediaManifest(string mediaRoot)
    {
        GeneratedArtifactManifestService.PublishDirectoryManifest(
            mediaRoot,
            "winpe-recovery-media",
            WinPeMediaRole);
        return GeneratedArtifactManifestService.VerifyDirectory(mediaRoot);
    }

    private static string WinPeMediaRole(string relativePath)
    {
        if (relativePath.Equals("sources/boot.wim", StringComparison.OrdinalIgnoreCase)) return "boot-image";
        if (relativePath.StartsWith("NVMe_Recovery_Kit/", StringComparison.OrdinalIgnoreCase)) return "embedded-recovery-kit";
        if (relativePath.Equals(
                $"{WinPEMediaFreshnessService.ControllerDirectoryName}/{WinPEMediaFreshnessService.ReportFileName}",
                StringComparison.OrdinalIgnoreCase)) return "controller-coverage-report";
        if (relativePath.EndsWith("Load_Controller_Drivers.bat", StringComparison.OrdinalIgnoreCase)) return "manual-driver-loader";
        if (relativePath.StartsWith(
                $"{WinPEMediaFreshnessService.ControllerDirectoryName}/Packages/",
                StringComparison.OrdinalIgnoreCase)) return "manual-controller-driver";
        if (relativePath.StartsWith("boot/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith("efi/", StringComparison.OrdinalIgnoreCase)) return "bootloader";
        return "winpe-runtime";
    }

    private static async Task RunProcessAsync(string file, string[] args, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"{file} did not start.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));
        var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var output = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(file)} {string.Join(' ', args)} exit {proc.ExitCode}: {output[1].Trim()} {output[0].Trim()}".Trim());
        }
    }
}
