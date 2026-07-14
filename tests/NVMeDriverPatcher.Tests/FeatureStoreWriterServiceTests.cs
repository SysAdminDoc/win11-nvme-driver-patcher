using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class FeatureStoreWriterServiceTests
{
    [Fact]
    public async Task RunExclusive_LockTimeoutReturnsBusyWithoutInvokingProtectedAction()
    {
        var mutexName = $@"Local\NVMeDriverPatcher.FeatureStore.Tests.{Guid.NewGuid():N}";
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, mutexName);
            mutex.WaitOne();
            acquired.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            var actionInvoked = false;
            var result = FeatureStoreWriterService.RunExclusive(
                () =>
                {
                    actionInvoked = true;
                    return new FeatureStoreWriteResult { Success = true };
                },
                TimeSpan.FromMilliseconds(50),
                mutexName);

            Assert.False(actionInvoked);
            Assert.False(result.Success);
            Assert.True(result.Busy);
            Assert.Equal(FeatureStoreWriteStatus.Busy, result.Status);
            Assert.Contains("no protected state was read or written", result.Summary);
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void ExactRestoreUpdate_RecreatesAllCompactStateFields()
    {
        const uint priority = 5;
        const uint enabled = 1;
        const uint wexp = 1;
        const uint variant = 37;
        const uint payloadKind = 2;
        const uint payload = 0xAABBCCDD;
        uint compact = priority | (enabled << 4) | (wexp << 6) | (variant << 8) | (payloadKind << 14);
        var baseline = new FeatureStoreConfigurationBaseline
        {
            FeatureId = 60786016,
            BootStore = true,
            Found = true,
            CompactState = compact,
            VariantPayload = payload
        };

        var update = FeatureStoreWriterService.DescribeRestoreUpdate(baseline);

        Assert.Equal(priority, update.Priority);
        Assert.Equal(enabled, update.EnabledState);
        Assert.Equal(wexp, update.EnabledStateOptions);
        Assert.Equal(variant, update.Variant);
        Assert.Equal(payloadKind, update.VariantPayloadKind);
        Assert.Equal(payload, update.VariantPayload);
        Assert.Equal(3u, update.Operation);
    }

    [Fact]
    public void ExactRestoreUpdate_AbsentBaselineUsesResetSemantics()
    {
        var update = FeatureStoreWriterService.DescribeRestoreUpdate(new FeatureStoreConfigurationBaseline
        {
            FeatureId = 60786016,
            Found = false
        });

        Assert.Equal(8u, update.Priority);
        Assert.Equal(4u, update.Operation);
    }
    [Fact]
    public void IndexOfBytes_FindsNeedleInMiddle()
    {
        byte[] hay = { 1, 2, 3, 4, 5, 6, 7, 8 };
        byte[] needle = { 4, 5 };
        Assert.Equal(3, FeatureStoreWriterService.IndexOfBytes(hay, needle));
    }

    [Fact]
    public void IndexOfBytes_ReturnsNegativeOneWhenMissing()
    {
        byte[] hay = { 1, 2, 3 };
        byte[] needle = { 9, 9 };
        Assert.Equal(-1, FeatureStoreWriterService.IndexOfBytes(hay, needle));
    }

    [Fact]
    public void IndexOfBytes_EmptyNeedleReturnsNegativeOne()
    {
        byte[] hay = { 1, 2, 3 };
        Assert.Equal(-1, FeatureStoreWriterService.IndexOfBytes(hay, Array.Empty<byte>()));
    }

    [Fact]
    public void WriteOverrides_NonAdmin_FailsClosedWithoutThrowing()
    {
        // The native writer is real now (RtlSetFeatureConfigurations). From a non-elevated
        // test host the kernel rejects the write — the contract is: no exception, no
        // success claim, and a message that routes the user somewhere actionable.
        // (On an elevated host this would mutate feature state, so we only assert the
        // failure path when the call did fail.)
        var result = FeatureStoreWriterService.WriteOverrides(new[] { 60786016, 48433719 });
        if (!result.Success)
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Summary));
            Assert.Contains("fallback", result.Summary, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Elevated environment: the write went through and must report what it applied.
            Assert.Equal(new[] { 60786016, 48433719 }, result.AppliedIds);
        }
    }

    [Fact]
    public void WriteOverrides_EmptyIdList_Refuses()
    {
        var result = FeatureStoreWriterService.WriteOverrides(Array.Empty<int>());
        Assert.False(result.Success);
    }

    [Fact]
    public void PostBlockFeatureIds_MatchesPublishedIds()
    {
        // Tom's Hardware / HotHardware / gamegpu community tracking: 60786016 + 48433719 are
        // the two IDs the ViVeTool fallback writes. Pinning here so a typo doesn't silently
        // invalidate everyone's fallback evidence check.
        Assert.Contains(60786016, FeatureStoreWriterService.PostBlockFeatureIds);
        Assert.Contains(48433719, FeatureStoreWriterService.PostBlockFeatureIds);
    }
    // --- Rtl native path: pure decode/construction logic + no-throw query probe ---

    [Theory]
    // CompactState bitfield: Priority:4 | EnabledState:2 | ...
    [InlineData(0x00u, 0, 0)]   // priority ImageDefault, state Default
    [InlineData(0x28u, 8, 2)]   // priority User(8), state Enabled(2) — what ViVeTool writes
    [InlineData(0x18u, 8, 1)]   // priority User(8), state Disabled(1)
    [InlineData(0x2Fu, 15, 2)]  // priority ImageOverride(15), state Enabled
    public void CompactState_DecodesPriorityAndEnabledState(uint compact, int expectedPriority, int expectedState)
    {
        Assert.Equal(expectedPriority, FeatureStoreWriterService.DecodePriority(compact));
        Assert.Equal(expectedState, FeatureStoreWriterService.DecodeEnabledState(compact));
    }

    [Fact]
    public void EnableUpdates_MatchViVeToolEnableSemantics()
    {
        // Priority User(8), state Enabled(2), Operation FeatureState|VariantState(3) —
        // diverging from what ViVeTool writes would create a mixed-priority store.
        var updates = FeatureStoreWriterService.DescribeEnableUpdates(new[] { 55369237, 48433719 });
        Assert.Equal(2, updates.Length);
        foreach (var u in updates)
        {
            Assert.Equal(8u, u.Priority);
            Assert.Equal(2u, u.EnabledState);
            Assert.Equal(3u, u.Operation);
        }
        Assert.Equal(55369237u, updates[0].FeatureId);
    }

    [Fact]
    public void QueryConfiguration_NeverThrows_AndReportsStore()
    {
        // Read-only Rtl query needs no admin. A random unconfigured ID must come back as
        // Found=false (or a valid state on machines where it IS configured) — never throw.
        var boot = FeatureStoreWriterService.QueryConfiguration(123456789, bootStore: true);
        var runtime = FeatureStoreWriterService.QueryConfiguration(123456789, bootStore: false);
        Assert.Equal("Boot", boot.Store);
        Assert.Equal("Runtime", runtime.Store);
        Assert.False(boot.IsEnabled && boot.EnabledState != 2); // IsEnabled implies state 2
    }

    [Fact]
    public void QueryAllKnownConfigurations_CoversEveryKnownIdInBothStores()
    {
        var states = FeatureStoreWriterService.QueryAllKnownConfigurations();
        Assert.Equal(FeatureStoreWriterService.PostBlockFeatureIds.Length * 2, states.Count);
        foreach (var id in FeatureStoreWriterService.PostBlockFeatureIds)
        {
            Assert.Contains(states, s => s.FeatureId == id && s.Store == "Boot");
            Assert.Contains(states, s => s.FeatureId == id && s.Store == "Runtime");
        }
    }

    // --- Both-store verification classifier (Runtime + Boot must BOTH be enabled) ---

    [Fact]
    public void ClassifyVerification_BothStoresEnabled_ReportsSuccess()
    {
        var statuses = new[]
        {
            new FeatureStoreIdStatus(60786016, RuntimeEnabled: true, BootEnabled: true),
            new FeatureStoreIdStatus(48433719, RuntimeEnabled: true, BootEnabled: true),
        };
        var result = FeatureStoreWriterService.ClassifyVerification(statuses);
        Assert.True(result.Success);
        Assert.Equal(new[] { 60786016, 48433719 }, result.AppliedIds);
        Assert.Equal(statuses, result.IdStatuses);
    }

    [Fact]
    public void ClassifyVerification_RuntimeOnly_FailsAndNamesBootGap()
    {
        // The dangerous case the prior Runtime-only check missed: Runtime enabled, Boot not.
        var statuses = new[]
        {
            new FeatureStoreIdStatus(60786016, RuntimeEnabled: true, BootEnabled: false),
        };
        var result = FeatureStoreWriterService.ClassifyVerification(statuses);
        Assert.False(result.Success);
        Assert.Empty(result.AppliedIds);
        Assert.Contains("Boot: NOT enabled", result.Summary);
        Assert.Contains("after reboot", result.Summary);
    }

    [Fact]
    public void ClassifyVerification_BootOnly_FailsAndNamesRuntimeGap()
    {
        var statuses = new[]
        {
            new FeatureStoreIdStatus(60786016, RuntimeEnabled: false, BootEnabled: true),
        };
        var result = FeatureStoreWriterService.ClassifyVerification(statuses);
        Assert.False(result.Success);
        Assert.Empty(result.AppliedIds);
        Assert.Contains("Runtime: NOT enabled", result.Summary);
    }

    [Fact]
    public void ClassifyVerification_NeitherStore_Fails()
    {
        var statuses = new[]
        {
            new FeatureStoreIdStatus(60786016, RuntimeEnabled: false, BootEnabled: false),
        };
        var result = FeatureStoreWriterService.ClassifyVerification(statuses);
        Assert.False(result.Success);
        Assert.Empty(result.AppliedIds);
        Assert.Contains("Runtime: NOT enabled", result.Summary);
        Assert.Contains("Boot: NOT enabled", result.Summary);
    }

    [Fact]
    public void ClassifyVerification_PartialAcrossIds_ReportsOnlyFullyEnabledAsApplied()
    {
        var statuses = new[]
        {
            new FeatureStoreIdStatus(60786016, RuntimeEnabled: true, BootEnabled: true),
            new FeatureStoreIdStatus(48433719, RuntimeEnabled: true, BootEnabled: false),
        };
        var result = FeatureStoreWriterService.ClassifyVerification(statuses);
        Assert.False(result.Success);
        Assert.Equal(new[] { 60786016 }, result.AppliedIds);
        Assert.Contains("48433719", result.Summary);
    }

    // --- Fallback reset/undo (rollback coverage) ---

    [Fact]
    public void ResetUpdates_MatchViVeToolResetSemantics()
    {
        // ViVeTool /reset: priority User(8), Operation ResetState(4), state Default(0).
        var updates = FeatureStoreWriterService.DescribeResetUpdates(new[] { 60786016, 48433719 });
        Assert.Equal(2, updates.Length);
        foreach (var u in updates)
        {
            Assert.Equal(8u, u.Priority);
            Assert.Equal(0u, u.EnabledState);
            Assert.Equal(4u, u.Operation);
        }
    }

    [Fact]
    public void ResetOverrides_EmptyIdList_Refuses()
    {
        var result = FeatureStoreWriterService.ResetOverrides(Array.Empty<int>());
        Assert.False(result.Success);
    }

    [Fact]
    public void ClassifyResetVerification_BothStoresCleared_ReportsSuccess()
    {
        var statuses = new[]
        {
            new FeatureStoreIdStatus(60786016, RuntimeEnabled: false, BootEnabled: false),
            new FeatureStoreIdStatus(48433719, RuntimeEnabled: false, BootEnabled: false),
        };
        var result = FeatureStoreWriterService.ClassifyResetVerification(statuses);
        Assert.True(result.Success);
        Assert.Equal(new[] { 60786016, 48433719 }, result.AppliedIds);
    }

    [Fact]
    public void ClassifyResetVerification_StillEnabled_FailsAndNamesStore()
    {
        var statuses = new[]
        {
            new FeatureStoreIdStatus(60786016, RuntimeEnabled: true, BootEnabled: false),
        };
        var result = FeatureStoreWriterService.ClassifyResetVerification(statuses);
        Assert.False(result.Success);
        Assert.Empty(result.AppliedIds);
        Assert.Contains("still enabled", result.Summary);
    }

    [Fact]
    public void ResetAppliedFallback_NoEvidenceOnHost_ReportsNothingToUndo()
    {
        // The "no applied IDs" case. CI/dev hosts have no NVMe fallback flags enabled, so this
        // reports a clean no-op. Environment-aware, mirroring WriteOverrides_NonAdmin: if a host
        // unexpectedly HAS them enabled the call still must not throw and must produce a summary.
        var result = FeatureStoreWriterService.ResetAppliedFallback();
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
        if (!FeatureStoreWriterService.HasFallbackEvidence())
        {
            Assert.True(result.Success);
            Assert.Contains("nothing to undo", result.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(result.AppliedIds);
        }
    }
}
