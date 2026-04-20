using System.IO;
using System.Text;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public class MinidumpSummary
{
    public string FilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedUtc { get; set; }
    public bool MentionsNVMeStack { get; set; }
    public List<string> MatchedModules { get; set; } = new();
    public string Notes { get; set; } = string.Empty;
}

public class MinidumpTriageReport
{
    public int TotalFound { get; set; }
    public int NewerThanPatch { get; set; }
    public int NVMeRelated { get; set; }
    public DateTime? OldestAnalyzed { get; set; }
    public DateTime? NewestAnalyzed { get; set; }
    public List<MinidumpSummary> Dumps { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
    public bool ScanCompleted { get; set; }
}

// Scans C:\Windows\Minidump for BSOD dumps newer than the patch apply timestamp. Does a
// lightweight string-match on each dump's readable sections for NVMe-stack module names —
// not a full !analyze -v (that needs WinDbg/dbghelp + symbol server). Enough to flag
// "this crash probably involves the driver you just enabled" without shipping 200MB of
// debug tooling.
public static class MinidumpTriageService
{
    private static readonly string[] DefaultDumpPaths =
    {
        @"C:\Windows\Minidump",
        @"C:\Windows\LiveKernelReports"
    };

    // Module substrings we match (case-insensitive) inside the dump's byte stream. Strings
    // table in a kernel minidump almost always contains module names as ASCII — good enough
    // for a correlation check.
    private static readonly string[] NVMeModules =
    {
        "nvmedisk.sys",
        "stornvme.sys",
        "storport.sys",
        "storahci.sys",
        "disk.sys",
        "partmgr.sys",
        "volmgr.sys"
    };

    // Cap per-file read to avoid spending minutes on a 2GB LiveKernelReport. 8MB of the
    // header is where the module table typically lives.
    private const int MaxBytesToScan = 8 * 1024 * 1024;

    public static MinidumpTriageReport Analyze(DateTime? patchAppliedAt)
    {
        var report = new MinidumpTriageReport();
        var candidates = new List<FileInfo>();

        foreach (var path in DefaultDumpPaths)
        {
            try
            {
                if (!Directory.Exists(path)) continue;
                var dir = new DirectoryInfo(path);
                foreach (var pattern in new[] { "*.dmp", "*.mdmp" })
                {
                    try { candidates.AddRange(dir.GetFiles(pattern, SearchOption.TopDirectoryOnly)); }
                    catch { /* permission denial on a subset — keep what we can see */ }
                }
            }
            catch { /* dir enumeration denied */ }
        }

        report.TotalFound = candidates.Count;
        if (candidates.Count == 0)
        {
            report.ScanCompleted = true;
            report.Summary = "No minidumps present. Clean slate.";
            return report;
        }

        // Only inspect dumps newer than the patch — older ones predate the change and
        // their stack traces can't be blamed on the swap.
        var cutoff = patchAppliedAt ?? DateTime.UtcNow - TimeSpan.FromDays(30);
        var relevant = candidates
            .Where(f => f.LastWriteTimeUtc >= cutoff)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(20)  // never analyse more than 20 dumps — bounds wall-clock cost
            .ToList();

        report.NewerThanPatch = relevant.Count;

        foreach (var file in relevant)
        {
            var dump = AnalyzeOne(file);
            report.Dumps.Add(dump);
            if (dump.MentionsNVMeStack) report.NVMeRelated++;
        }

        report.OldestAnalyzed = report.Dumps.Select(d => d.CreatedUtc).DefaultIfEmpty().Min();
        report.NewestAnalyzed = report.Dumps.Select(d => d.CreatedUtc).DefaultIfEmpty().Max();
        report.ScanCompleted = true;
        report.Summary = BuildSummary(report);
        return report;
    }

    private static MinidumpSummary AnalyzeOne(FileInfo file)
    {
        var summary = new MinidumpSummary
        {
            FilePath = file.FullName,
            SizeBytes = file.Length,
            CreatedUtc = file.LastWriteTimeUtc
        };

        try
        {
            using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int readLen = (int)Math.Min(MaxBytesToScan, fs.Length);
            var buffer = new byte[readLen];
            int offset = 0;
            while (offset < readLen)
            {
                int read = fs.Read(buffer, offset, readLen - offset);
                if (read <= 0) break;
                offset += read;
            }
            if (offset <= 0)
            {
                summary.Notes = "Empty or unreadable dump file.";
                return summary;
            }
            // UTF-8 covers the ASCII module-name strings; latin1 would also work.
            // We lowercase once and search without further allocs.
            var haystack = Encoding.ASCII.GetString(buffer, 0, offset).ToLowerInvariant();
            foreach (var mod in NVMeModules)
            {
                if (haystack.Contains(mod, StringComparison.Ordinal))
                {
                    summary.MatchedModules.Add(mod);
                }
            }
            summary.MentionsNVMeStack = summary.MatchedModules.Count > 0;
            if (summary.MentionsNVMeStack)
                summary.Notes = $"NVMe stack modules referenced: {string.Join(", ", summary.MatchedModules)}";
            else
                summary.Notes = "No NVMe-stack module references in the first 8MB of the dump.";
        }
        catch (Exception ex)
        {
            summary.Notes = $"Scan failed: {ex.GetType().Name}";
        }

        return summary;
    }

    internal static string BuildSummary(MinidumpTriageReport report)
    {
        if (report.NewerThanPatch == 0)
            return $"No new crash dumps since patch. {report.TotalFound} older dump(s) on disk.";
        if (report.NVMeRelated == 0)
            return $"{report.NewerThanPatch} new crash dump(s) since patch, none reference the NVMe stack.";
        return $"{report.NVMeRelated}/{report.NewerThanPatch} post-patch crash dump(s) reference the NVMe stack — investigate.";
    }
}
