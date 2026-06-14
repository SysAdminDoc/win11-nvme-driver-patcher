using System.Diagnostics;
using System.IO;
using System.Text;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

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

        if (!IsAdkAvailable(out var winPeRoot))
        {
            result.Success = false;
            result.Summary = "Windows ADK + WinPE add-on not detected. Install from: " + AdkDownloadUrl;
            return result;
        }

        if (string.IsNullOrWhiteSpace(options.OutputDir))
        {
            result.Success = false;
            result.Summary = "OutputDir not set.";
            return result;
        }

        try
        {
            Directory.CreateDirectory(options.OutputDir);
            var treeDir = Path.Combine(options.OutputDir, "NVMePatcher_WinPE");
            if (Directory.Exists(treeDir))
            {
                log?.Invoke($"[INFO] Purging stale tree at {treeDir}");
                try { Directory.Delete(treeDir, recursive: true); }
                catch (Exception ex) { result.Warnings.Add($"Could not delete stale tree: {ex.Message}"); }
            }
            result.TreeDir = treeDir;

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
                new[] { "/c", copypeCmd, options.Architecture, treeDir },
                timeoutSeconds: 120,
                cancellationToken: cancellationToken);

            // boot.wim is always produced by copype — set it unconditionally so kit-less builds
            // still report the wim path (previously this was only set inside the Recovery-Kit copy).
            result.WimPath = Path.Combine(treeDir, "media", "sources", "boot.wim");

            // Copy the Recovery Kit onto the media so it's reachable as a data folder from WinPE.
            if (!string.IsNullOrWhiteSpace(options.RecoveryKitDir) && Directory.Exists(options.RecoveryKitDir))
            {
                var kitOut = Path.Combine(treeDir, "media", "NVMe_Recovery_Kit");
                try
                {
                    CopyDir(options.RecoveryKitDir, kitOut);
                    log?.Invoke($"[INFO] Recovery Kit copied to {kitOut}");
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Recovery Kit copy failed: {ex.Message}");
                }
            }

            // Inject startnet.cmd INTO boot.wim's \Windows\System32 so it actually runs at WinPE
            // boot. The old code wrote it to <tree>\media\sources\startnet.cmd, which WinPE never
            // reads — it only ever runs the copy inside the image, so the recovery announcement
            // silently never appeared on the exact can't-boot path the stick exists for.
            await InjectStartnetIntoBootWimAsync(treeDir, result, log, cancellationToken);

            if (options.ProduceIso)
            {
                var makeMedia = Path.Combine(winPeRoot!, "MakeWinPEMedia.cmd");
                var isoPath = Path.Combine(options.OutputDir, "NVMePatcher_Recovery.iso");
                log?.Invoke("[INFO] Producing ISO via MakeWinPEMedia /ISO...");
                await RunProcessAsync(
                    "cmd.exe",
                    new[] { "/c", makeMedia, "/ISO", treeDir, isoPath },
                    timeoutSeconds: 300,
                    cancellationToken: cancellationToken);
                if (File.Exists(isoPath))
                {
                    result.IsoPath = isoPath;
                    log?.Invoke($"[SUCCESS] ISO written to {isoPath}");
                }
                else
                {
                    result.Warnings.Add("MakeWinPEMedia returned 0 but ISO file is missing.");
                }
            }

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
        return content.ToString();
    }

    // Writes startnet.cmd into a mounted image tree (pure filesystem — the testable seam).
    internal static void WriteStartnetToMount(string mountDir)
    {
        var target = StartnetTargetPath(mountDir);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, BuildStartnetContent(), Encoding.ASCII);
    }

    // Dism /Mount-Image boot.wim -> write startnet.cmd into \Windows\System32 -> /Unmount /Commit.
    // Injection failure degrades to a warning (the user still gets a bootable stick) rather than
    // aborting the whole build.
    private static async Task InjectStartnetIntoBootWimAsync(
        string treeDir, WinPEBuildResult result, Action<string>? log, CancellationToken ct)
    {
        var bootWim = Path.Combine(treeDir, "media", "sources", "boot.wim");
        var mountDir = Path.Combine(treeDir, "mount");
        if (!File.Exists(bootWim))
        {
            result.Warnings.Add($"boot.wim missing at {bootWim}; startnet.cmd recovery announcement NOT injected.");
            return;
        }

        bool mounted = false;
        try
        {
            Directory.CreateDirectory(mountDir);
            log?.Invoke("[INFO] Mounting boot.wim to inject startnet.cmd...");
            await RunProcessAsync("dism.exe",
                new[] { "/Mount-Image", $"/ImageFile:{bootWim}", "/Index:1", $"/MountDir:{mountDir}" },
                timeoutSeconds: 300, cancellationToken: ct);
            mounted = true;

            WriteStartnetToMount(mountDir);
            log?.Invoke(@"[INFO] startnet.cmd injected into boot.wim\Windows\System32.");
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Could not inject startnet.cmd into boot.wim ({ex.Message}); " +
                "the stick will boot to a bare WinPE prompt without recovery instructions.");
        }
        finally
        {
            if (mounted)
            {
                try
                {
                    await RunProcessAsync("dism.exe",
                        new[] { "/Unmount-Image", $"/MountDir:{mountDir}", "/Commit" },
                        timeoutSeconds: 300, cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Unmount/commit of boot.wim failed ({ex.Message}); " +
                        "run 'Dism /Cleanup-Mountpoints'. The startnet.cmd injection may not have persisted.");
                    try
                    {
                        await RunProcessAsync("dism.exe",
                            new[] { "/Unmount-Image", $"/MountDir:{mountDir}", "/Discard" },
                            timeoutSeconds: 120, cancellationToken: CancellationToken.None);
                    }
                    catch { }
                }
            }
            try { if (Directory.Exists(mountDir)) Directory.Delete(mountDir, recursive: true); } catch { }
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
        if (proc.ExitCode != 0)
        {
            var err = await stderr;
            var outp = await stdout;
            throw new InvalidOperationException(
                $"{Path.GetFileName(file)} {string.Join(' ', args)} exit {proc.ExitCode}: {err.Trim()} {outp.Trim()}".Trim());
        }
    }
}
