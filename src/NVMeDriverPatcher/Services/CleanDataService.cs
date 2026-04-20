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
