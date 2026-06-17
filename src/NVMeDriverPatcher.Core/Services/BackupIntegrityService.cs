using System.Diagnostics;
using System.IO;
using System.Text;

namespace NVMeDriverPatcher.Services;

public class BackupIntegrityResult
{
    public bool Success { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string? ReportPath { get; set; }
    public int ValueCount { get; set; }
}

// Best-effort integrity check on a .reg backup: verifies the Windows Registry Editor header
// and counts the `[HKLM\…]` section headers + `"name"=` or `@=` value assignments. Intentionally
// text-only — a real `reg import` would overwrite live paths (the .reg's own targets are real
// HKLM locations), which is exactly the failure the backup is meant to protect against.
//
// A historical revision of this code eagerly created and deleted an HKLM\SYSTEM\NVMeBackupVerifyStaging
// scratch subkey, believing it would be used for a future dry-run import. That import path was
// never wired up (it would have been destructive anyway), so the staging key was pure registry
// pollution on every verify. Removed in v4.7.
public static class BackupIntegrityService
{
    public static BackupIntegrityResult Verify(string regFilePath, Action<string>? log = null)
    {
        var result = new BackupIntegrityResult();
        if (!File.Exists(regFilePath))
        {
            result.Summary = $"Backup not found: {regFilePath}";
            return result;
        }

        // Sanity-check the .reg header so we don't treat obvious garbage as a valid backup.
        try
        {
            using var sr = new StreamReader(regFilePath);
            var header = sr.ReadLine() ?? string.Empty;
            if (!header.StartsWith("Windows Registry Editor", StringComparison.Ordinal) &&
                !header.StartsWith("REGEDIT4", StringComparison.Ordinal))
            {
                result.Summary = "Backup file missing the Windows Registry Editor header — not a valid .reg export.";
                return result;
            }
        }
        catch (Exception ex)
        {
            result.Summary = $"Backup file unreadable: {ex.Message}";
            return result;
        }

        try
        {
            int sections = 0, values = 0;
            foreach (var line in File.ReadLines(regFilePath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]")) sections++;
                else if (trimmed.Contains('=') && (trimmed.StartsWith("\"") || trimmed.StartsWith("@"))) values++;
            }
            result.ValueCount = values;
            if (sections == 0)
            {
                result.Summary = "Backup file contains no registry key sections.";
                return result;
            }

            result.Success = true;
            result.Summary = $"Backup verified: {sections} key section(s), {values} value assignment(s).";
            log?.Invoke($"[OK] {result.Summary}");
        }
        catch (Exception ex)
        {
            result.Summary = $"Backup verification failed: {ex.Message}";
        }
        return result;
    }
}
