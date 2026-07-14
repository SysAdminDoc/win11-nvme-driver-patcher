using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

/// <summary>A single registry value captured from a SafeBoot key.</summary>
public sealed record SafeBootValueSnapshot(string Name, int Kind, string? StringData);

/// <summary>Point-in-time state of one SafeBoot key, enough to classify it and to restore it
/// byte-for-byte on removal.</summary>
public sealed record SafeBootKeySnapshot
{
    public string Path { get; init; } = string.Empty;
    public bool Existed { get; init; }
    public bool AccessDenied { get; init; }
    public IReadOnlyList<SafeBootValueSnapshot> Values { get; init; } = Array.Empty<SafeBootValueSnapshot>();

    /// <summary>The default (unnamed) value's string data, or null when absent.</summary>
    public string? DefaultValue =>
        Values.FirstOrDefault(v => v.Name.Length == 0)?.StringData;

    /// <summary>Named values (e.g. the OS-shipped "NvmeDisk" REG_SZ on build 26200.8737).</summary>
    public bool HasForeignNamedValues => Values.Any(v => v.Name.Length > 0);
}

public enum SafeBootKeyDisposition
{
    /// <summary>Key is absent — a clean create. Whatever we create, we own and may delete.</summary>
    WritableAbsent,
    /// <summary>Key exists with exactly our expected default and nothing else — idempotent.</summary>
    AlreadyCorrect,
    /// <summary>Key exists with a different default value than we expect.</summary>
    ConflictingDefault,
    /// <summary>Key exists with named values we did not write (OS-owned — issue #13).</summary>
    ForeignValuesPresent,
    /// <summary>The key cannot be read/written (ACL denies this process — issue #13).</summary>
    AccessDenied
}

public sealed record SafeBootRestorePlan(bool DeleteEntireKey, bool DeleteAppDefaultValue, string? RestorePriorDefault);

/// <summary>Read/write seam over the SafeBoot registry so the transaction logic is unit-testable
/// with an in-memory fake and never has to touch the live boot-critical keys in tests.</summary>
public interface ISafeBootRegistry
{
    SafeBootKeySnapshot Read(string path);
    void ApplyRestore(string path, SafeBootRestorePlan plan);
}

public sealed class SafeBootJournalEntry
{
    public string Path { get; set; } = string.Empty;
    public string ExpectedDefault { get; set; } = string.Empty;
    public bool Existed { get; set; }
    public bool AccessDenied { get; set; }
    public List<SafeBootValueSnapshot> Values { get; set; } = new();

    public SafeBootKeySnapshot ToSnapshot() => new()
    {
        Path = Path,
        Existed = Existed,
        AccessDenied = AccessDenied,
        Values = Values
    };
}

public sealed class SafeBootJournal
{
    public int SchemaVersion { get; set; } = 1;
    public string CapturedUtc { get; set; } = string.Empty;
    public List<SafeBootJournalEntry> Entries { get; set; } = new();
}

/// <summary>
/// Treats SafeBoot edits as a reversible transaction. Before applying, it CAPTURES the exact prior
/// state of every SafeBoot key the patch touches; on removal it restores that state byte-for-byte,
/// deleting ONLY the values/keys the app created. It never deletes an OS-owned key (e.g. the
/// pre-existing GUID key carrying a "NvmeDisk" value on build 26200.8737 — GitHub issue #13) and
/// never changes ACLs. Classification is exposed to preflight so denied/conflicting/foreign keys are
/// surfaced before any feature write.
/// </summary>
public static class SafeBootStateService
{
    public const string JournalFileName = "safeboot_journal.json";

    // The keys the patch manages, paired with the default value each expects.
    public static IReadOnlyList<(string Path, string ExpectedDefault)> ManagedKeys { get; } = new[]
    {
        (AppConfig.SafeBootMinimalPath, AppConfig.SafeBootValue),
        (AppConfig.SafeBootNetworkPath, AppConfig.SafeBootValue),
        (AppConfig.SafeBootMinimalServicePath, AppConfig.SafeBootServiceValue),
        (AppConfig.SafeBootNetworkServicePath, AppConfig.SafeBootServiceValue),
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Pure classification of a captured key against the value we expect to write.</summary>
    public static SafeBootKeyDisposition Classify(SafeBootKeySnapshot snapshot, string expectedDefault)
    {
        if (snapshot.AccessDenied) return SafeBootKeyDisposition.AccessDenied;
        if (!snapshot.Existed) return SafeBootKeyDisposition.WritableAbsent;
        if (snapshot.HasForeignNamedValues) return SafeBootKeyDisposition.ForeignValuesPresent;

        var def = snapshot.DefaultValue;
        if (def is null) return SafeBootKeyDisposition.WritableAbsent; // exists but empty — we can set our default
        return string.Equals(def, expectedDefault, StringComparison.OrdinalIgnoreCase)
            ? SafeBootKeyDisposition.AlreadyCorrect
            : SafeBootKeyDisposition.ConflictingDefault;
    }

    /// <summary>
    /// Pure restore planner from the PRE-APPLY snapshot. Deletes the whole key only when the app
    /// created it (it did not exist before); otherwise it never removes the key and restores the
    /// prior default exactly, leaving foreign named values untouched.
    /// </summary>
    public static SafeBootRestorePlan PlanRestore(SafeBootKeySnapshot priorState)
    {
        if (!priorState.Existed)
            return new SafeBootRestorePlan(DeleteEntireKey: true, DeleteAppDefaultValue: false, RestorePriorDefault: null);

        var priorDefault = priorState.DefaultValue;
        return priorDefault is null
            ? new SafeBootRestorePlan(DeleteEntireKey: false, DeleteAppDefaultValue: true, RestorePriorDefault: null)
            : new SafeBootRestorePlan(DeleteEntireKey: false, DeleteAppDefaultValue: false, RestorePriorDefault: priorDefault);
    }

    /// <summary>Capture the prior state of every managed key.</summary>
    public static SafeBootJournal CaptureJournal(ISafeBootRegistry registry, string capturedUtc)
    {
        var journal = new SafeBootJournal { CapturedUtc = capturedUtc };
        foreach (var (path, expected) in ManagedKeys)
        {
            var snap = registry.Read(path);
            journal.Entries.Add(new SafeBootJournalEntry
            {
                Path = path,
                ExpectedDefault = expected,
                Existed = snap.Existed,
                AccessDenied = snap.AccessDenied,
                Values = snap.Values.ToList()
            });
        }
        return journal;
    }

    /// <summary>Restore every journalled key to its captured pre-apply state. Returns the paths that
    /// could not be fully restored (empty = clean).</summary>
    public static List<string> RestoreFromJournal(ISafeBootRegistry registry, SafeBootJournal journal, Action<string>? log = null)
    {
        var failures = new List<string>();
        foreach (var entry in journal.Entries)
        {
            try
            {
                var plan = PlanRestore(entry.ToSnapshot());
                registry.ApplyRestore(entry.Path, plan);
                log?.Invoke($"  [SafeBoot] Restored {entry.Path}: " +
                    (plan.DeleteEntireKey ? "removed app-created key"
                     : plan.DeleteAppDefaultValue ? "removed app default value, kept pre-existing key/values"
                     : $"restored prior default '{plan.RestorePriorDefault}'"));
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.Path} ({ex.GetType().Name})");
                log?.Invoke($"  [SafeBoot] FAILED to restore {entry.Path}: {ex.Message}");
            }
        }
        return failures;
    }

    public static string JournalPath(string workingDir) => Path.Combine(workingDir, JournalFileName);

    public static bool SaveJournal(
        string workingDir,
        SafeBootJournal journal,
        Action<string>? log = null,
        bool preserveExistingBaseline = true)
    {
        string? tmp = null;
        try
        {
            Directory.CreateDirectory(workingDir);
            var target = JournalPath(workingDir);
            if (preserveExistingBaseline && LoadJournal(workingDir) is not null)
                return true;

            tmp = target + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var json = JsonSerializer.Serialize(journal, JsonOptions);
            using (var fs = new FileStream(
                       tmp,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            var staged = JsonSerializer.Deserialize<SafeBootJournal>(File.ReadAllText(tmp));
            if (staged is null || staged.Entries.Count != journal.Entries.Count)
                throw new InvalidDataException("Staged SafeBoot journal validation failed.");

            if (File.Exists(target))
                File.Replace(tmp, target, target + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(tmp, target);
            return true;
        }
        catch (Exception ex)
        {
            try { if (tmp is not null && File.Exists(tmp)) File.Delete(tmp); } catch { }
            log?.Invoke($"  [SafeBoot] Could not persist SafeBoot journal: {ex.Message}");
            return false;
        }
    }

    public static SafeBootJournal? LoadJournal(string workingDir)
    {
        try
        {
            var path = JournalPath(workingDir);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<SafeBootJournal>(json);
        }
        catch { return null; }
    }

    public static void DeleteJournal(string workingDir)
    {
        try { var p = JournalPath(workingDir); if (File.Exists(p)) File.Delete(p); } catch { }
    }

    /// <summary>Classify the two boot-critical GUID keys for preflight, using the live registry.</summary>
    public static (SafeBootKeyDisposition Minimal, SafeBootKeyDisposition Network) ClassifyGuidKeys(ISafeBootRegistry registry)
    {
        var min = Classify(registry.Read(AppConfig.SafeBootMinimalPath), AppConfig.SafeBootValue);
        var net = Classify(registry.Read(AppConfig.SafeBootNetworkPath), AppConfig.SafeBootValue);
        return (min, net);
    }
}

/// <summary>Live HKLM (64-bit view) implementation of the SafeBoot registry seam.</summary>
public sealed class RealSafeBootRegistry : ISafeBootRegistry
{
    public SafeBootKeySnapshot Read(string path)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(path, writable: false);
            if (key is null)
                return new SafeBootKeySnapshot { Path = path, Existed = false };

            var values = new List<SafeBootValueSnapshot>();
            foreach (var name in key.GetValueNames())
            {
                var kind = key.GetValueKind(name);
                var raw = key.GetValue(name);
                values.Add(new SafeBootValueSnapshot(name, (int)kind, raw?.ToString()));
            }
            return new SafeBootKeySnapshot { Path = path, Existed = true, Values = values };
        }
        catch (System.Security.SecurityException)
        {
            return new SafeBootKeySnapshot { Path = path, Existed = true, AccessDenied = true };
        }
        catch (UnauthorizedAccessException)
        {
            return new SafeBootKeySnapshot { Path = path, Existed = true, AccessDenied = true };
        }
    }

    public void ApplyRestore(string path, SafeBootRestorePlan plan)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        if (plan.DeleteEntireKey)
        {
            var (parentPath, leaf) = SplitLeaf(path);
            using var parent = hklm.OpenSubKey(parentPath, writable: true);
            parent?.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
            return;
        }

        using var key = hklm.OpenSubKey(path, writable: true);
        if (key is null) return; // nothing to restore into

        if (plan.DeleteAppDefaultValue)
        {
            try { key.DeleteValue("", throwOnMissingValue: false); } catch { }
        }
        else if (plan.RestorePriorDefault is not null)
        {
            key.SetValue("", plan.RestorePriorDefault, RegistryValueKind.String);
        }
        try { key.Flush(); } catch { }
    }

    private static (string Parent, string Leaf) SplitLeaf(string path)
    {
        var idx = path.LastIndexOf('\\');
        return idx <= 0 ? (string.Empty, path) : (path[..idx], path[(idx + 1)..]);
    }
}
