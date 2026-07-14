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

        public void Set(string path, SafeBootKeySnapshot snap) => _state[path] = snap with { Path = path };

        public SafeBootKeySnapshot Read(string path) =>
            _state.TryGetValue(path, out var s) ? s : new SafeBootKeySnapshot { Path = path, Existed = false };

        public void ApplyRestore(string path, SafeBootRestorePlan plan)
        {
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
