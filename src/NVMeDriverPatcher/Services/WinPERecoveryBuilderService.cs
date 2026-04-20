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

            // Copy the Recovery Kit into the media's root so it appears in both WinPE and Windows.
            if (!string.IsNullOrWhiteSpace(options.RecoveryKitDir) && Directory.Exists(options.RecoveryKitDir))
            {
                var mediaRoot = Path.Combine(treeDir, "media");
                var kitOut = Path.Combine(mediaRoot, "NVMe_Recovery_Kit");
                try
                {
                    CopyDir(options.RecoveryKitDir, kitOut);
                    log?.Invoke($"[INFO] Recovery Kit copied to {kitOut}");
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Recovery Kit copy failed: {ex.Message}");
                }
                result.WimPath = Path.Combine(treeDir, "media", "sources", "boot.wim");
            }

            // Add a startnet.cmd that auto-announces the Recovery Kit on boot.
            try
            {
                var startnet = Path.Combine(treeDir, "media", "sources", "startnet.cmd");
                var content = new StringBuilder();
                content.AppendLine("@echo off");
                content.AppendLine("wpeinit");
                content.AppendLine();
                content.AppendLine("echo ====================================================");
                content.AppendLine("echo  NVMe Driver Patcher - WinPE Recovery");
                content.AppendLine("echo ====================================================");
                content.AppendLine("echo.");
                content.AppendLine("echo To remove the patch, run:");
                content.AppendLine("echo   X:");
                content.AppendLine("echo   cd \\NVMe_Recovery_Kit");
                content.AppendLine("echo   Remove_NVMe_Patch.bat");
                content.AppendLine("echo.");
                File.WriteAllText(startnet, content.ToString(), Encoding.ASCII);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"startnet.cmd write failed: {ex.Message}");
            }

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
