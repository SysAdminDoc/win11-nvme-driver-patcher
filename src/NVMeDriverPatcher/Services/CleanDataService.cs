using System.IO;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class CleanDataResult
{
    public bool Success { get; set; }
    public long BytesFreed { get; set; }
    public int FilesRemoved { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = new();
}

// Purges the per-user working directory (crash logs, watchdog state, ETL captures, snapshot
// DB). Separate from the patch uninstaller — removing the patch doesn't and shouldn't wipe
// the user's history. Intended for final "uninstall the app" flow + explicit CLI invocation.
public static class CleanDataService
{
    // Named subsets a user can target individually. "All" is the default — the CLI accepts
    // --targets=logs,etl,backups,db if the user wants finer control.
    public static readonly string[] AllTargets =
    {
        "logs",      // *.log, *.log.1..5 in working dir
        "etl",       // etl\*.etl
        "backups",   // Pre_*_Backup_*.reg
        "db",        // nvmepatcher.db*
        "bundles",   // support_bundle_*.zip
        "staging"    // tools\staging\, compat_report.json, anon_id.txt
    };

    public static CleanDataResult Clean(AppConfig config, IEnumerable<string>? targets = null)
    {
        var result = new CleanDataResult();
        var selected = (targets ?? AllTargets).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dir = string.IsNullOrWhiteSpace(config.WorkingDir) ? AppConfig.GetWorkingDir() : config.WorkingDir;

        // Scope guard: this method recursively deletes tools\ and pattern-matched files
        // under `dir`. A corrupted config or bad portable flag pointing at a drive root or
        // a system directory must never turn that into a system-wide sweep.
        if (!IsSafeCleanRoot(dir, out var reason))
        {
            result.Success = false;
            result.Summary = $"Refusing to clean '{dir}': {reason}";
            result.Errors.Add(result.Summary);
            return result;
        }

        if (!Directory.Exists(dir))
        {
            result.Success = true;
            result.Summary = $"Nothing to clean — {dir} does not exist.";
            return result;
        }

        if (selected.Contains("logs"))
            Sweep(Directory.EnumerateFiles(dir, "*.log*", SearchOption.TopDirectoryOnly), result);
        if (selected.Contains("etl"))
        {
            var etl = Path.Combine(dir, "etl");
            if (Directory.Exists(etl))
                SweepTree(etl, result);
        }
        if (selected.Contains("backups"))
            Sweep(Directory.EnumerateFiles(dir, "Pre_*_Backup_*.reg", SearchOption.TopDirectoryOnly), result);
        if (selected.Contains("db"))
            Sweep(Directory.EnumerateFiles(dir, "nvmepatcher.db*", SearchOption.TopDirectoryOnly), result);
        if (selected.Contains("bundles"))
            Sweep(Directory.EnumerateFiles(dir, "support_bundle_*.zip", SearchOption.TopDirectoryOnly), result);
        if (selected.Contains("staging"))
        {
            var tools = Path.Combine(dir, "tools");
            if (Directory.Exists(tools)) SweepTree(tools, result);
            foreach (var f in new[] { "compat_report.json", "anon_id.txt" })
            {
                var p = Path.Combine(dir, f);
                if (File.Exists(p)) TryDelete(p, result);
            }
        }

        result.Success = result.Errors.Count == 0;
        result.Summary = $"Removed {result.FilesRemoved} file(s), freed {result.BytesFreed / 1024.0 / 1024.0:F2} MB from {dir}.";
        return result;
    }

    /// <summary>
    /// A clean root is safe when it is a real, absolute, non-root path that is NOT a
    /// protected system location. Both the default %LocalAppData%\NVMePatcher dir and
    /// portable-mode exe-adjacent dirs pass; drive roots, Windows, Program Files, and
    /// user-profile roots refuse.
    /// </summary>
    internal static bool IsSafeCleanRoot(string? dir, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(dir)) { reason = "path is empty"; return false; }

        // "C:" (no slash) resolves via Path.GetFullPath to the drive's CURRENT DIRECTORY —
        // wherever the process happens to be. That ambiguity is unacceptable for a
        // recursive delete root; require a real directory path.
        var trimmed = dir.Trim();
        if (trimmed.Length == 2 && trimmed[1] == ':' && char.IsLetter(trimmed[0]))
        {
            reason = "drive-letter-only path is ambiguous (resolves to the process's current directory)";
            return false;
        }

        string full;
        try { full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception ex) { reason = $"path is invalid ({ex.Message})"; return false; }

        if (!Path.IsPathRooted(full)) { reason = "path is not absolute"; return false; }

        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            reason = "path is a drive root";
            return false;
        }

        // App-managed roots are always cleanable even though they sit under a protected root:
        // the default %LocalAppData%\NVMePatcher (and the TEMP scratch area) live under the user
        // profile, and a portable install's Data\ dir lives beside the exe (possibly under
        // Program Files). Anything strictly under one of these passes before the subtree guard.
        foreach (var safe in SafeAppRoots())
        {
            if (string.IsNullOrEmpty(safe)) continue;
            var s = safe.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.StartsWith(s + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Protected locations: cleaning must never target these OR any subtree beneath them.
        // (Previously only the Windows directory refused subtrees, so a portable install dropped
        // directly under Program Files or the user profile passed the guard.)
        var protectedDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        foreach (var p in protectedDirs)
        {
            if (string.IsNullOrEmpty(p)) continue;
            var prot = p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, prot, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"path is a protected system location ({prot})";
                return false;
            }
            // Refuse anything beneath a protected root too (defense-in-depth).
            if (full.StartsWith(prot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"path is inside a protected system location ({prot})";
                return false;
            }
        }

        return true;
    }

    // App-managed roots under which cleaning is always permitted (the per-user LocalAppData tree,
    // which also contains TEMP, and the portable exe directory). Computed without side effects.
    private static IEnumerable<string> SafeAppRoots()
    {
        string? localApp = null;
        try { localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
        catch { }
        if (!string.IsNullOrEmpty(localApp)) yield return localApp;

        string? exeDir = null;
        try { exeDir = AppContext.BaseDirectory; }
        catch { }
        if (!string.IsNullOrEmpty(exeDir)) yield return exeDir;
    }

    private static void Sweep(IEnumerable<string> files, CleanDataResult result)
    {
        foreach (var file in files) TryDelete(file, result);
    }

    private static void SweepTree(string root, CleanDataResult result)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                TryDelete(file, result);
        }
        catch (Exception ex) { result.Errors.Add($"Tree sweep {root}: {ex.Message}"); }
    }

    private static void TryDelete(string path, CleanDataResult result)
    {
        try
        {
            var info = new FileInfo(path);
            var size = info.Exists ? info.Length : 0;
            File.Delete(path);
            result.FilesRemoved++;
            result.BytesFreed += size;
        }
        catch (Exception ex) { result.Errors.Add($"{path}: {ex.Message}"); }
    }
}
