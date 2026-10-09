using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>The key exists, is readable, and TrustedInstaller owns it. 24H2 26100.9550 ships the
    /// GUID keys this way with "NvmeDisk" as the default value, and even SYSTEM can't write them.</summary>
    public bool WindowsOwned { get; init; }
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
    AccessDenied,
    /// <summary>Windows created the key, owns it through TrustedInstaller and write-protects it.
    /// It already registers the driver for Safe Mode, so apply leaves it alone.</summary>
    WindowsOwned
}

/// <summary>What removal does to one key. <paramref name="RestorePriorDefaultKind"/> is the
/// registry kind the prior default had (a <see cref="RegistryValueKind"/>), so a REG_EXPAND_SZ or
/// REG_DWORD default goes back as what it was rather than as a string.</summary>
public sealed record SafeBootRestorePlan(
    bool DeleteEntireKey,
    bool DeleteAppDefaultValue,
    string? RestorePriorDefault,
    int RestorePriorDefaultKind = (int)RegistryValueKind.String);

/// <summary>
/// Text form of a SafeBoot value's data, by kind, so a snapshot round-trips byte for byte.
/// Strings keep their raw text (environment names unexpanded), numbers are invariant decimal,
/// multi-strings are joined with NUL as the registry stores them, and bytes are hex.
/// </summary>
internal static class SafeBootValueCodec
{
    public static string? Encode(RegistryValueKind kind, object? raw) => raw switch
    {
        null => null,
        string text => text,
        string[] parts => string.Join('\0', parts),
        byte[] bytes => Convert.ToHexString(bytes),
        int or long or uint or ulong => Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture),
        _ => raw.ToString()
    };

    /// <summary>
    /// What a journal from before schema 2 (<see cref="SafeBootJournal.CurrentSchemaVersion"/>)
    /// holds for a value whose live data is <paramref name="data"/>. Those journals kept
    /// <c>GetValue(name)?.ToString()</c>: REG_EXPAND_SZ text with its environment names expanded,
    /// "System.String[]" for every REG_MULTI_SZ and "System.Byte[]" for every REG_BINARY. A live
    /// key has to be put through the same lens before it's compared with one of them, or a key
    /// nobody touched reads as residue.
    /// </summary>
    public static string? LegacyEncode(int kind, string? data)
    {
        if (data is null) return null;
        return (RegistryValueKind)kind switch
        {
            RegistryValueKind.ExpandString => Environment.ExpandEnvironmentVariables(data),
            RegistryValueKind.MultiString => "System.String[]",
            RegistryValueKind.Binary or RegistryValueKind.None or RegistryValueKind.Unknown => "System.Byte[]",
            _ => data
        };
    }

    /// <summary>The value and kind to hand <c>RegistryKey.SetValue</c>. Data that doesn't parse as
    /// its recorded kind is written as the string it was recorded as, which is what the old code
    /// did for everything.</summary>
    public static (object Value, RegistryValueKind Kind) Decode(int kind, string data)
    {
        var valueKind = (RegistryValueKind)kind;
        switch (valueKind)
        {
            case RegistryValueKind.ExpandString:
                return (data, valueKind);
            case RegistryValueKind.MultiString:
                return (data.Length == 0 ? Array.Empty<string>() : data.Split('\0'), valueKind);
            case RegistryValueKind.DWord:
                if (int.TryParse(data, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var dword))
                    return (dword, valueKind);
                break;
            case RegistryValueKind.QWord:
                if (long.TryParse(data, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var qword))
                    return (qword, valueKind);
                break;
            case RegistryValueKind.Binary:
            case RegistryValueKind.None:
                try { return (Convert.FromHexString(data), valueKind); }
                catch (FormatException) { }
                break;
        }
        return (data, RegistryValueKind.String);
    }
}

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
    public bool WindowsOwned { get; set; }
    public List<SafeBootValueSnapshot> Values { get; set; } = new();

    /// <summary>Set when the journal was read from schema 1, whose value data is encoded the way
    /// <see cref="SafeBootValueCodec.LegacyEncode"/> describes. Not stored: the journal's
    /// <see cref="SafeBootJournal.SchemaVersion"/> is what says it.</summary>
    [JsonIgnore]
    public bool LegacyValueEncoding { get; set; }

    public SafeBootKeySnapshot ToSnapshot() => new()
    {
        Path = Path,
        Existed = Existed,
        AccessDenied = AccessDenied,
        WindowsOwned = WindowsOwned,
        Values = Values
    };
}

public sealed class SafeBootJournal : IJsonOnDeserialized
{
    /// <summary>Schema 2 records value data with <see cref="SafeBootValueCodec"/>; schema 1 held
    /// <c>GetValue(name)?.ToString()</c>. The property's default stays 1 so a journal written
    /// before the property existed reads as what it is.</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = 1;
    public string CapturedUtc { get; set; } = string.Empty;
    public List<SafeBootJournalEntry> Entries { get; set; } = new();

    /// <summary>Runs for the journal file and for the copy inside the mutation ledger alike, so
    /// every entry read from an older schema knows how its values are encoded.</summary>
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (SchemaVersion >= CurrentSchemaVersion) return;
        foreach (var entry in Entries) entry.LegacyValueEncoding = true;
    }
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
        if (snapshot.WindowsOwned) return SafeBootKeyDisposition.WindowsOwned;
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

        var prior = priorState.Values.FirstOrDefault(v => v.Name.Length == 0);
        return prior?.StringData is null
            ? new SafeBootRestorePlan(DeleteEntireKey: false, DeleteAppDefaultValue: true, RestorePriorDefault: null)
            : new SafeBootRestorePlan(DeleteEntireKey: false, DeleteAppDefaultValue: false, RestorePriorDefault: prior.StringData, RestorePriorDefaultKind: prior.Kind);
    }

    /// <summary>Capture the prior state of every managed key.</summary>
    /// <summary>
    /// Pure: every SafeBoot key the patch manages, including the per-control-set mirrors written
    /// so a boot-recovery promotion cannot drop them (issue #15). Mirroring the journal as well as
    /// the writes is what keeps uninstall byte-exact — restore iterates the journal, so a mirrored
    /// key that never entered the journal would survive removal.
    /// </summary>
    public static IReadOnlyList<(string Path, string ExpectedDefault)> ManagedKeysFor(
        IReadOnlyList<string>? mirrorControlSets)
    {
        if (mirrorControlSets is not { Count: > 0 }) return ManagedKeys;

        var keys = new List<(string Path, string ExpectedDefault)>(ManagedKeys);
        foreach (var controlSet in mirrorControlSets)
        {
            foreach (var (path, expected) in ManagedKeys)
            {
                var mirrored = ControlSetService.MirrorPath(path, controlSet);
                if (mirrored is not null) keys.Add((mirrored, expected));
            }
        }
        return keys;
    }

    public static SafeBootJournal CaptureJournal(ISafeBootRegistry registry, string capturedUtc) =>
        CaptureJournal(registry, capturedUtc, mirrorControlSets: null);

    public static SafeBootJournal CaptureJournal(
        ISafeBootRegistry registry,
        string capturedUtc,
        IReadOnlyList<string>? mirrorControlSets)
    {
        var journal = new SafeBootJournal { SchemaVersion = SafeBootJournal.CurrentSchemaVersion, CapturedUtc = capturedUtc };
        foreach (var (path, expected) in ManagedKeysFor(mirrorControlSets))
        {
            var snap = registry.Read(path);
            journal.Entries.Add(new SafeBootJournalEntry
            {
                Path = path,
                ExpectedDefault = expected,
                Existed = snap.Existed,
                AccessDenied = snap.AccessDenied,
                WindowsOwned = snap.WindowsOwned,
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
            if (entry.WindowsOwned)
            {
                // Apply never writes a Windows-owned key, so there's nothing to undo, and the
                // write would be refused anyway.
                log?.Invoke($"  [Safe Boot] Left {entry.Path} as is: Windows owns and write-protects it");
                continue;
            }

            try
            {
                var plan = PlanRestore(entry.ToSnapshot());
                registry.ApplyRestore(entry.Path, plan);
                log?.Invoke($"  [Safe Boot] Restored {entry.Path}: " +
                    (plan.DeleteEntireKey ? "removed app-created key"
                     : plan.DeleteAppDefaultValue ? "removed app default value, kept pre-existing key/values"
                     : $"restored prior default '{plan.RestorePriorDefault}'"));
            }
            catch (Exception ex) when (HarmlessRefusal(registry, entry, ex) is string reason)
            {
                log?.Invoke($"  [Safe Boot] Left {entry.Path} as is: {reason}");
            }
            catch (Exception ex)
            {
                failures.Add($"{entry.Path} ({ex.GetType().Name})");
                log?.Invoke($"  [Safe Boot] FAILED to restore {entry.Path}: {ex.Message}");
            }
        }
        return failures;
    }

    /// <summary>Why a refused restore left nothing to undo, or null when the refusal is a real failure.</summary>
    private static string? HarmlessRefusal(ISafeBootRegistry registry, SafeBootJournalEntry entry, Exception ex)
    {
        if (ex is not (UnauthorizedAccessException or System.Security.SecurityException)) return null;
        try
        {
            var live = registry.Read(entry.Path);
            // Journals written before ownership was recorded still list Windows-owned keys. A
            // refused write to a key that already matches its baseline had nothing to undo.
            if (SnapshotsMatch(entry.ToSnapshot(), AsRecorded(entry, live)))
                return "write-protected and already at its pre-apply state";
            // Servicing can create or take over the key after the patch was applied (26100.9550
            // ships its own). Ownership alone isn't enough: a key that still holds this tool's
            // value is residue whoever owns it now.
            if (IsWindowsOwnedWithoutThisToolsValue(entry, live))
                return "Windows took it over after the patch was applied and write-protects it";
            return null;
        }
        catch { return null; }
    }

    /// <summary>True when a live key needs no restore: it matches its baseline, or Windows owns it
    /// now and it doesn't hold this tool's value. Remove's verification uses this so a key
    /// servicing took over doesn't read as residue.</summary>
    internal static bool IsAtBaselineOrWindowsOwned(SafeBootJournalEntry baseline, SafeBootKeySnapshot live) =>
        SnapshotsMatch(baseline.ToSnapshot(), AsRecorded(baseline, live)) || IsWindowsOwnedWithoutThisToolsValue(baseline, live);

    /// <summary>The live key as a journal of the baseline's schema would have recorded it. A schema 1
    /// baseline encoded REG_EXPAND_SZ, REG_MULTI_SZ and REG_BINARY data differently from today's
    /// read, so compared with a live read as is, a key nobody touched counted as residue.</summary>
    internal static SafeBootKeySnapshot AsRecorded(SafeBootJournalEntry baseline, SafeBootKeySnapshot live) =>
        baseline.LegacyValueEncoding
            ? live with { Values = live.Values.Select(v => v with { StringData = SafeBootValueCodec.LegacyEncode(v.Kind, v.StringData) }).ToList() }
            : live;

    private static bool IsWindowsOwnedWithoutThisToolsValue(SafeBootJournalEntry entry, SafeBootKeySnapshot live) =>
        live.WindowsOwned &&
        !string.Equals(live.DefaultValue, entry.ExpectedDefault, StringComparison.OrdinalIgnoreCase);

    /// <summary>Same existence, readability and values, in any value order. Ownership isn't
    /// compared: older journals never recorded it.</summary>
    internal static bool SnapshotsMatch(SafeBootKeySnapshot left, SafeBootKeySnapshot right)
    {
        if (left.Existed != right.Existed || left.AccessDenied != right.AccessDenied)
            return false;
        var l = left.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var r = right.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return l.SequenceEqual(r);
    }

    // NT SERVICE\TrustedInstaller. A SID, so the check doesn't depend on the display language.
    internal const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    /// <summary>True when TrustedInstaller owns the key. Reading the owner needs only the read
    /// access the key was opened with, so this works without elevation and writes nothing.</summary>
    internal static bool IsTrustedInstallerOwned(RegistryKey key)
    {
        try
        {
            var owner = key.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            return string.Equals(owner?.Value, TrustedInstallerSid, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string JournalPath(string workingDir) =>
        Path.Combine(AppConfig.GetPrivilegedStateDirectory(workingDir), JournalFileName);

    public static bool SaveJournal(
        string workingDir,
        SafeBootJournal journal,
        Action<string>? log = null,
        bool preserveExistingBaseline = true)
    {
        string? tmp = null;
        try
        {
            var access = PrivilegedStateSecurityService.EnsureForMutation(workingDir);
            if (!access.Success)
                throw new UnauthorizedAccessException(access.Summary);
            var target = JournalPath(workingDir);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
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
                throw new InvalidDataException("Staged Safe Boot journal validation failed.");

            if (File.Exists(target))
                File.Replace(tmp, target, target + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(tmp, target);
            if (AppConfig.IsRuntimeWorkingDirectory(workingDir))
            {
                var protectedPrimary = PrivilegedStateSecurityService.ProtectCriticalFile(
                    target, StateDirectoryRole.Privileged);
                if (!protectedPrimary.Success)
                    throw new IOException(protectedPrimary.Summary);
                var backup = target + ".bak";
                if (File.Exists(backup))
                {
                    var protectedBackup = PrivilegedStateSecurityService.ProtectCriticalFile(
                        backup, StateDirectoryRole.Privileged);
                    if (!protectedBackup.Success)
                        throw new IOException(protectedBackup.Summary);
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            try { if (tmp is not null && File.Exists(tmp)) File.Delete(tmp); } catch { }
            log?.Invoke($"  [Safe Boot] Could not persist Safe Boot journal: {ex.Message}");
            return false;
        }
    }

    public static SafeBootJournal? LoadJournal(string workingDir)
    {
        try
        {
            var access = PrivilegedStateSecurityService.EnsureForMutation(workingDir);
            if (!access.Success) return null;
            var path = JournalPath(workingDir);
            if (!File.Exists(path)) return null;
            if (AppConfig.IsRuntimeWorkingDirectory(workingDir) &&
                !PrivilegedStateSecurityService.ValidateCriticalFile(path, StateDirectoryRole.Privileged).Success)
                return null;
            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<SafeBootJournal>(json);
        }
        catch { return null; }
    }

    public static void DeleteJournal(string workingDir)
    {
        try
        {
            var access = PrivilegedStateSecurityService.EnsureForMutation(workingDir);
            if (!access.Success) return;
            var p = JournalPath(workingDir);
            if (File.Exists(p) &&
                (!AppConfig.IsRuntimeWorkingDirectory(workingDir) ||
                 PrivilegedStateSecurityService.ValidateCriticalFile(p, StateDirectoryRole.Privileged).Success))
                File.Delete(p);
        }
        catch { }
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
            return ReadKey(key, path);
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

    /// <summary>Snapshot of an opened key (null means absent). Environment names in
    /// REG_EXPAND_SZ data stay as written, so the journal holds what the registry holds.</summary>
    internal static SafeBootKeySnapshot ReadKey(RegistryKey? key, string path)
    {
        if (key is null)
            return new SafeBootKeySnapshot { Path = path, Existed = false };

        var values = new List<SafeBootValueSnapshot>();
        foreach (var name in key.GetValueNames())
        {
            var kind = key.GetValueKind(name);
            var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            values.Add(new SafeBootValueSnapshot(name, (int)kind, SafeBootValueCodec.Encode(kind, raw)));
        }
        return new SafeBootKeySnapshot
        {
            Path = path,
            Existed = true,
            WindowsOwned = SafeBootStateService.IsTrustedInstallerOwned(key),
            Values = values
        };
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
        RestoreDefault(key, plan);
    }

    /// <summary>The default-value half of a restore, against an opened writable key.</summary>
    internal static void RestoreDefault(RegistryKey key, SafeBootRestorePlan plan)
    {
        if (plan.DeleteAppDefaultValue)
        {
            try { key.DeleteValue("", throwOnMissingValue: false); } catch { }
        }
        else if (plan.RestorePriorDefault is not null)
        {
            var (value, kind) = SafeBootValueCodec.Decode(plan.RestorePriorDefaultKind, plan.RestorePriorDefault);
            key.SetValue("", value, kind);
        }
        try { key.Flush(); } catch { }
    }

    private static (string Parent, string Leaf) SplitLeaf(string path)
    {
        var idx = path.LastIndexOf('\\');
        return idx <= 0 ? (string.Empty, path) : (path[..idx], path[(idx + 1)..]);
    }
}
