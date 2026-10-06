using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class SafeBootStateServiceTests
{
    private const string ExpectedDefault = AppConfig.SafeBootValue; // "Storage Disks"

    // In-memory registry fake so the boot-critical transaction logic is exercised without touching
    // the live SafeBoot keys.
    private sealed class FakeSafeBootRegistry : ISafeBootRegistry
    {
        private readonly Dictionary<string, SafeBootKeySnapshot> _state = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string Path, SafeBootRestorePlan Plan)> Applied = new();
        // Keys whose writes are refused, the way TrustedInstaller-owned keys refuse even SYSTEM.
        public readonly HashSet<string> WriteProtected = new(StringComparer.OrdinalIgnoreCase);

        public void Set(string path, SafeBootKeySnapshot snap) => _state[path] = snap with { Path = path };

        public SafeBootKeySnapshot Read(string path) =>
            _state.TryGetValue(path, out var s) ? s : new SafeBootKeySnapshot { Path = path, Existed = false };

        public void ApplyRestore(string path, SafeBootRestorePlan plan)
        {
            if (WriteProtected.Contains(path))
                throw new UnauthorizedAccessException($"Access to the registry key '{path}' is denied.");
            Applied.Add((path, plan));
            // Simulate the mutation so a follow-up Read reflects the restore.
            if (plan.DeleteEntireKey) { _state.Remove(path); return; }
            var snap = Read(path);
            var values = snap.Values.ToList();
            if (plan.DeleteAppDefaultValue)
                values.RemoveAll(v => v.Name.Length == 0);
            else if (plan.RestorePriorDefault is not null)
            {
                values.RemoveAll(v => v.Name.Length == 0);
                values.Add(new SafeBootValueSnapshot("", 1, plan.RestorePriorDefault));
            }
            Set(path, snap with { Existed = true, Values = values });
        }
    }

    private static SafeBootKeySnapshot Absent() => new() { Existed = false };
    private static SafeBootKeySnapshot Denied() => new() { Existed = true, AccessDenied = true };
    private static SafeBootKeySnapshot WithDefault(string value) =>
        new() { Existed = true, Values = new[] { new SafeBootValueSnapshot("", 1, value) } };
    private static SafeBootKeySnapshot WithNamed(string name, string value) =>
        new() { Existed = true, Values = new[] { new SafeBootValueSnapshot(name, 1, value) } };
    private static SafeBootKeySnapshot EmptyKey() =>
        new() { Existed = true, Values = Array.Empty<SafeBootValueSnapshot>() };

    // --- Classification: empty, correct, NvmeDisk (foreign), conflict, denied ---

    [Fact]
    public void Classify_Absent_IsWritableAbsent() =>
        Assert.Equal(SafeBootKeyDisposition.WritableAbsent, SafeBootStateService.Classify(Absent(), ExpectedDefault));

    [Fact]
    public void Classify_CorrectDefault_IsAlreadyCorrect() =>
        Assert.Equal(SafeBootKeyDisposition.AlreadyCorrect, SafeBootStateService.Classify(WithDefault(ExpectedDefault), ExpectedDefault));

    [Fact]
    public void Classify_NvmeDiskNamedValue_IsForeignValuesPresent() =>
        Assert.Equal(SafeBootKeyDisposition.ForeignValuesPresent, SafeBootStateService.Classify(WithNamed("NvmeDisk", "Storage Disks"), ExpectedDefault));

    [Fact]
    public void Classify_DifferentDefault_IsConflictingDefault() =>
        Assert.Equal(SafeBootKeyDisposition.ConflictingDefault, SafeBootStateService.Classify(WithDefault("Something Else"), ExpectedDefault));

    [Fact]
    public void Classify_AccessDenied_IsAccessDenied() =>
        Assert.Equal(SafeBootKeyDisposition.AccessDenied, SafeBootStateService.Classify(Denied(), ExpectedDefault));

    [Fact]
    public void Classify_EmptyKey_IsWritableAbsent() =>
        Assert.Equal(SafeBootKeyDisposition.WritableAbsent, SafeBootStateService.Classify(EmptyKey(), ExpectedDefault));

    // --- Restore planning: only app-created state is removed ---

    [Fact]
    public void PlanRestore_AppCreatedKey_DeletesEntireKey()
    {
        var plan = SafeBootStateService.PlanRestore(Absent());
        Assert.True(plan.DeleteEntireKey);
    }

    [Fact]
    public void PlanRestore_PreexistingForeignKey_NeverDeletesKey_RemovesOnlyOurDefault()
    {
        // Issue #13: key existed with a "NvmeDisk" named value and no default.
        var plan = SafeBootStateService.PlanRestore(WithNamed("NvmeDisk", "Storage Disks"));
        Assert.False(plan.DeleteEntireKey);
        Assert.True(plan.DeleteAppDefaultValue);
        Assert.Null(plan.RestorePriorDefault);
    }

    [Fact]
    public void PlanRestore_PreexistingDefault_RestoresItByteForByte()
    {
        var plan = SafeBootStateService.PlanRestore(WithDefault("Prior Value"));
        Assert.False(plan.DeleteEntireKey);
        Assert.False(plan.DeleteAppDefaultValue);
        Assert.Equal("Prior Value", plan.RestorePriorDefault);
    }

    // --- End-to-end journal capture + restore against the fake ---

    [Fact]
    public void CaptureThenRestore_Issue13_PreservesOsOwnedKey()
    {
        var reg = new FakeSafeBootRegistry();
        // Windows already shipped the Minimal GUID key with a "NvmeDisk" value (issue #13).
        reg.Set(AppConfig.SafeBootMinimalPath, WithNamed("NvmeDisk", "Storage Disks"));
        // Network GUID key absent; service keys absent.

        var journal = SafeBootStateService.CaptureJournal(reg, "2026-07-14T00:00:00Z");

        // Simulate apply writing our default onto the pre-existing key + creating the absent one.
        reg.Set(AppConfig.SafeBootMinimalPath, new SafeBootKeySnapshot
        {
            Existed = true,
            Values = new[]
            {
                new SafeBootValueSnapshot("NvmeDisk", 1, "Storage Disks"),
                new SafeBootValueSnapshot("", 1, ExpectedDefault)
            }
        });
        reg.Set(AppConfig.SafeBootNetworkPath, WithDefault(ExpectedDefault));

        var failures = SafeBootStateService.RestoreFromJournal(reg, journal);
        Assert.Empty(failures);

        // The OS-owned Minimal key still exists and retains NvmeDisk; our default is gone.
        var min = reg.Read(AppConfig.SafeBootMinimalPath);
        Assert.True(min.Existed);
        Assert.Contains(min.Values, v => v.Name == "NvmeDisk");
        Assert.Null(min.DefaultValue);

        // The app-created Network key is gone entirely.
        Assert.False(reg.Read(AppConfig.SafeBootNetworkPath).Existed);
    }

    [Fact]
    public void CaptureThenRestore_PreexistingCorrectDefault_IsPreserved()
    {
        var reg = new FakeSafeBootRegistry();
        // The OS/user already had our exact default before we applied.
        reg.Set(AppConfig.SafeBootMinimalPath, WithDefault(ExpectedDefault));

        var journal = SafeBootStateService.CaptureJournal(reg, "2026-07-14T00:00:00Z");
        SafeBootStateService.RestoreFromJournal(reg, journal);

        // We must not remove pre-existing correct state.
        Assert.Equal(ExpectedDefault, reg.Read(AppConfig.SafeBootMinimalPath).DefaultValue);
    }

    [Fact]
    public void JournalRoundTrips_ThroughDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.SafeBoot.{Guid.NewGuid():N}");
        try
        {
            var reg = new FakeSafeBootRegistry();
            reg.Set(AppConfig.SafeBootMinimalPath, WithNamed("NvmeDisk", "Storage Disks"));
            var journal = SafeBootStateService.CaptureJournal(reg, "2026-07-14T00:00:00Z");

            Assert.True(SafeBootStateService.SaveJournal(dir, journal));
            var loaded = SafeBootStateService.LoadJournal(dir);

            Assert.NotNull(loaded);
            var min = loaded!.Entries.First(e => e.Path == AppConfig.SafeBootMinimalPath);
            Assert.True(min.Existed);
            Assert.Contains(min.Values, v => v.Name == "NvmeDisk");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // --- Windows-owned keys: 24H2 26100.9550 ships the GUID keys owned by TrustedInstaller with
    // "NvmeDisk" as the default value, readable by everyone and writable by no one else ---

    private static SafeBootKeySnapshot WindowsOwnedNvmeDisk() => WithDefault("NvmeDisk") with { WindowsOwned = true };

    [Fact]
    public void Classify_WindowsOwnedKey_IsWindowsOwnedWhateverItHolds()
    {
        Assert.Equal(SafeBootKeyDisposition.WindowsOwned, SafeBootStateService.Classify(WindowsOwnedNvmeDisk(), ExpectedDefault));
        Assert.Equal(SafeBootKeyDisposition.WindowsOwned,
            SafeBootStateService.Classify(WithNamed("NvmeDisk", "Storage Disks") with { WindowsOwned = true }, ExpectedDefault));
        // An unreadable key stays AccessDenied, and a missing one can't be owned by anyone.
        Assert.Equal(SafeBootKeyDisposition.AccessDenied,
            SafeBootStateService.Classify(Denied() with { WindowsOwned = true }, ExpectedDefault));
        Assert.Equal(SafeBootKeyDisposition.WritableAbsent,
            SafeBootStateService.Classify(Absent() with { WindowsOwned = true }, ExpectedDefault));
        // The same value in a key this tool may write is still a conflict it overwrites.
        Assert.Equal(SafeBootKeyDisposition.ConflictingDefault, SafeBootStateService.Classify(WithDefault("NvmeDisk"), ExpectedDefault));
    }

    [Fact]
    public void CaptureThenRestore_SkipsWindowsOwnedKeysWithoutTryingToWrite()
    {
        var reg = new FakeSafeBootRegistry();
        reg.Set(AppConfig.SafeBootMinimalPath, WindowsOwnedNvmeDisk());
        reg.Set(AppConfig.SafeBootNetworkPath, WindowsOwnedNvmeDisk());
        reg.WriteProtected.Add(AppConfig.SafeBootMinimalPath);
        reg.WriteProtected.Add(AppConfig.SafeBootNetworkPath);

        var journal = SafeBootStateService.CaptureJournal(reg, "2026-10-05T00:00:00Z");
        Assert.True(journal.Entries.Single(e => e.Path == AppConfig.SafeBootMinimalPath).WindowsOwned);
        Assert.False(journal.Entries.Single(e => e.Path == AppConfig.SafeBootMinimalServicePath).WindowsOwned);

        var log = new List<string>();
        var failures = SafeBootStateService.RestoreFromJournal(reg, journal, log.Add);

        Assert.Empty(failures);
        Assert.DoesNotContain(reg.Applied, a => a.Path == AppConfig.SafeBootMinimalPath || a.Path == AppConfig.SafeBootNetworkPath);
        Assert.Equal(2, log.Count(line => line.Contains("Windows owns and write-protects it", StringComparison.Ordinal)));
        Assert.Equal("NvmeDisk", reg.Read(AppConfig.SafeBootMinimalPath).DefaultValue);
    }

    [Fact]
    public void Restore_FromAJournalThatPredatesOwnership_AcceptsARefusalOnlyWhenNothingChanged()
    {
        // Journals written before ownership was recorded say WindowsOwned = false for these keys.
        var reg = new FakeSafeBootRegistry();
        reg.Set(AppConfig.SafeBootMinimalPath, WithDefault("NvmeDisk"));
        reg.Set(AppConfig.SafeBootNetworkPath, WithDefault("NvmeDisk"));
        var journal = SafeBootStateService.CaptureJournal(reg, "2026-10-05T00:00:00Z");
        reg.WriteProtected.Add(AppConfig.SafeBootMinimalPath);
        reg.WriteProtected.Add(AppConfig.SafeBootNetworkPath);

        // Network changed after capture and now can't be put back: that's a real failure.
        reg.Set(AppConfig.SafeBootNetworkPath, WithDefault(ExpectedDefault));

        var failures = SafeBootStateService.RestoreFromJournal(reg, journal);

        var failure = Assert.Single(failures);
        Assert.StartsWith(AppConfig.SafeBootNetworkPath, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_KeyWindowsTookOverAfterApply_IsLeftAloneNotAFailure()
    {
        // Patched on a build without the GUID keys (baseline: absent), then servicing added its own
        // TrustedInstaller-owned keys. Remove plans a whole-key delete that Windows refuses; there's
        // nothing of this tool's left to undo, so it mustn't strand remove as a failure.
        var reg = new FakeSafeBootRegistry();
        var journal = SafeBootStateService.CaptureJournal(reg, "2026-10-05T00:00:00Z");
        reg.Set(AppConfig.SafeBootMinimalPath, WindowsOwnedNvmeDisk());
        reg.Set(AppConfig.SafeBootNetworkPath, WithDefault(ExpectedDefault));   // this tool's write
        reg.WriteProtected.Add(AppConfig.SafeBootMinimalPath);
        var log = new List<string>();

        var failures = SafeBootStateService.RestoreFromJournal(reg, journal, log.Add);

        Assert.Empty(failures);
        Assert.Contains(log, line => line.Contains(AppConfig.SafeBootMinimalPath, StringComparison.Ordinal) &&
                                     line.Contains("Windows took it over", StringComparison.Ordinal));
        Assert.True(reg.Read(AppConfig.SafeBootMinimalPath).WindowsOwned);
        Assert.False(reg.Read(AppConfig.SafeBootNetworkPath).Existed);   // ours is still removed
    }

    [Fact]
    public void BaselineCheck_AcceptsAWindowsOwnedKeyButNotThisToolsLeftover()
    {
        Assert.True(SafeBootStateService.IsAtBaselineOrWindowsOwned(Absent(), WindowsOwnedNvmeDisk()));
        Assert.True(SafeBootStateService.IsAtBaselineOrWindowsOwned(WithDefault("NvmeDisk"), WithDefault("NvmeDisk")));
        Assert.False(SafeBootStateService.IsAtBaselineOrWindowsOwned(Absent(), WithDefault(ExpectedDefault)));
        Assert.False(SafeBootStateService.IsAtBaselineOrWindowsOwned(WithDefault("NvmeDisk"), WithDefault(ExpectedDefault)));
    }

    [Fact]
    public void Journal_KeepsOwnershipThroughDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.SafeBoot.{Guid.NewGuid():N}");
        try
        {
            var reg = new FakeSafeBootRegistry();
            reg.Set(AppConfig.SafeBootMinimalPath, WindowsOwnedNvmeDisk());
            Assert.True(SafeBootStateService.SaveJournal(dir, SafeBootStateService.CaptureJournal(reg, "2026-10-05T00:00:00Z")));

            var loaded = SafeBootStateService.LoadJournal(dir);

            Assert.NotNull(loaded);
            Assert.True(loaded!.Entries.Single(e => e.Path == AppConfig.SafeBootMinimalPath).WindowsOwned);
            Assert.False(loaded.Entries.Single(e => e.Path == AppConfig.SafeBootNetworkPath).WindowsOwned);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void TrustedInstallerOwnership_IsReadFromTheLiveRegistryWithoutWriteAccess()
    {
        // "This PC" ships owned by TrustedInstaller on every Windows 10 and 11 build; the SafeBoot
        // Minimal parent key is owned by SYSTEM. Both are opened read-only.
        using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var thisPc = hklm.OpenSubKey(@"SOFTWARE\Classes\CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}", writable: false);
        using var safeBootMinimal = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal", writable: false);

        Assert.NotNull(thisPc);
        Assert.NotNull(safeBootMinimal);
        Assert.True(SafeBootStateService.IsTrustedInstallerOwned(thisPc!));
        Assert.False(SafeBootStateService.IsTrustedInstallerOwned(safeBootMinimal!));
    }

    [Fact]
    public void SaveJournal_ReapplyPreservesFirstCleanBaseline()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.SafeBoot.{Guid.NewGuid():N}");
        try
        {
            var first = new SafeBootJournal
            {
                CapturedUtc = "2026-07-14T00:00:00Z",
                Entries =
                [
                    new SafeBootJournalEntry
                    {
                        Path = AppConfig.SafeBootMinimalPath,
                        Existed = true,
                        Values = [new SafeBootValueSnapshot("", 1, "Original")]
                    }
                ]
            };
            var reapplied = new SafeBootJournal
            {
                CapturedUtc = "2026-07-14T01:00:00Z",
                Entries =
                [
                    new SafeBootJournalEntry
                    {
                        Path = AppConfig.SafeBootMinimalPath,
                        Existed = true,
                        Values = [new SafeBootValueSnapshot("", 1, AppConfig.SafeBootValue)]
                    }
                ]
            };

            Assert.True(SafeBootStateService.SaveJournal(dir, first));
            Assert.True(SafeBootStateService.SaveJournal(dir, reapplied));

            var loaded = SafeBootStateService.LoadJournal(dir);
            Assert.NotNull(loaded);
            Assert.Equal("Original", loaded!.Entries.Single().ToSnapshot().DefaultValue);
            Assert.Equal(first.CapturedUtc, loaded.CapturedUtc);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
