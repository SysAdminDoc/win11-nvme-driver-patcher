using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class BitLockerRecoveryServiceTests
{
    private const string ProtectorA = "{11111111-1111-1111-1111-111111111111}";
    private const string ProtectorB = "{22222222-2222-2222-2222-222222222222}";

    [Fact]
    public void Prepare_UnencryptedVolume_DoesNotRequireJoinOrSuspension()
    {
        var platform = new FakePlatform
        {
            Volume = Volume(conversionStatus: 0, protectionStatus: 0, ids: []),
            Join = new(false, DirectoryJoinKind.None, "probe unavailable")
        };
        bool persisted = false;

        var result = BitLockerRecoveryService.PrepareForMutation(
            platform,
            () => persisted = true);

        Assert.True(result.Success);
        Assert.False(result.SuspensionRequired);
        Assert.False(persisted);
        Assert.Empty(platform.Events);
    }

    [Fact]
    public void Prepare_EncryptedVolumeWithoutRecoveryProtector_FailsClosed()
    {
        var platform = new FakePlatform { Volume = Volume(ids: []) };

        var result = BitLockerRecoveryService.PrepareForMutation(platform, () => true);

        Assert.False(result.Success);
        Assert.Contains("no numerical-password", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(platform.Events);
    }

    [Theory]
    [InlineData(2u, 1u)]
    [InlineData(1u, 2u)]
    public void Prepare_UnstableOrUnknownState_FailsClosed(uint conversionStatus, uint protectionStatus)
    {
        var platform = new FakePlatform
        {
            Volume = Volume(conversionStatus, protectionStatus, [ProtectorA])
        };

        var result = BitLockerRecoveryService.PrepareForMutation(platform, () => true);

        Assert.False(result.Success);
        Assert.Empty(platform.Events);
    }

    [Fact]
    public void Prepare_EncryptedVolumeWithUnknownJoinState_FailsClosed()
    {
        var platform = new FakePlatform
        {
            Volume = Volume(ids: [ProtectorA]),
            Join = new(false, DirectoryJoinKind.None, "join probe failed")
        };

        var result = BitLockerRecoveryService.PrepareForMutation(platform, () => true);

        Assert.False(result.Success);
        Assert.Contains("join state unavailable", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prepare_HybridJoin_RefreshesEveryBackupBeforeDurableSuspendIntent()
    {
        var initial = Volume(ids: [ProtectorA, ProtectorB]);
        var suspended = Volume(protectionStatus: 0, suspendCount: 1, ids: [ProtectorA, ProtectorB]);
        var platform = new FakePlatform
        {
            Volume = initial,
            Join = new(true, DirectoryJoinKind.Hybrid)
        };
        platform.Inspections.Enqueue(initial);
        platform.Inspections.Enqueue(suspended);

        var result = BitLockerRecoveryService.PrepareForMutation(platform, () =>
        {
            platform.Events.Add("intent");
            return true;
        });

        Assert.True(result.Success);
        Assert.True(result.SuspendedByThisCall);
        Assert.Equal(
        [
            $"ad:{ProtectorA}", $"entra:C::{ProtectorA}",
            $"ad:{ProtectorB}", $"entra:C::{ProtectorB}",
            "intent", "suspend"
        ], platform.Events);
        Assert.True(result.Proof.Volume.IsSuspendedForOneReboot);
    }

    [Fact]
    public void Prepare_SuspendSuccessWithoutOneRebootProof_Fails()
    {
        var initial = Volume(ids: [ProtectorA]);
        var unverified = Volume(protectionStatus: 0, suspendCount: 0, ids: [ProtectorA]);
        var platform = new FakePlatform { Volume = initial };
        platform.Inspections.Enqueue(initial);
        platform.Inspections.Enqueue(unverified);

        var result = BitLockerRecoveryService.PrepareForMutation(platform, () => true);

        Assert.False(result.Success);
        Assert.True(result.SuspendedByThisCall);
        Assert.Contains("SuspendCount=1", result.Summary);
    }

    [Fact]
    public void Prepare_AlreadySuspendedForOneReboot_DoesNotClaimSuspensionOwnership()
    {
        var platform = new FakePlatform
        {
            Volume = Volume(protectionStatus: 0, suspendCount: 1, ids: [ProtectorA])
        };
        bool persisted = false;

        var result = BitLockerRecoveryService.PrepareForMutation(platform, () => persisted = true);

        Assert.True(result.Success);
        Assert.True(result.SuspensionRequired);
        Assert.False(result.SuspendedByThisCall);
        Assert.False(persisted);
        Assert.DoesNotContain("suspend", platform.Events);
    }

    [Fact]
    public void Resume_RequiresFreshProtectionStatusProof()
    {
        var suspended = Volume(protectionStatus: 0, suspendCount: 1, ids: [ProtectorA]);
        var resumed = Volume(protectionStatus: 1, ids: [ProtectorA]);
        var platform = new FakePlatform { Volume = suspended };
        platform.Inspections.Enqueue(suspended);
        platform.Inspections.Enqueue(resumed);

        var result = BitLockerRecoveryService.ResumeSystemVolume(platform);

        Assert.True(result.Success);
        Assert.Equal(["resume"], platform.Events);
    }

    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111", ProtectorA)]
    [InlineData(ProtectorA, ProtectorA)]
    [InlineData("not-a-protector", null)]
    [InlineData("123456-123456-123456-123456-123456-123456-123456-123456", null)]
    public void NormalizeProtectorId_AcceptsOnlyGuids(string input, string? expected)
    {
        Assert.Equal(expected, WmiBitLockerPlatform.NormalizeProtectorId(input));
    }

    private static BitLockerVolumeEvidence Volume(
        uint conversionStatus = 1,
        uint protectionStatus = 1,
        IReadOnlyList<string>? ids = null,
        uint? suspendCount = null) => new()
    {
        ProbeSucceeded = true,
        SystemVolumePresent = true,
        MountPoint = "C:",
        ConversionStatus = conversionStatus,
        ProtectionStatus = protectionStatus,
        SuspendCount = suspendCount,
        RecoveryProtectorIds = ids ?? [ProtectorA]
    };

    private sealed class FakePlatform : IBitLockerPlatform
    {
        public BitLockerVolumeEvidence Volume { get; set; } = BitLockerRecoveryServiceTests.Volume();
        public DirectoryJoinEvidence Join { get; set; } = new(true, DirectoryJoinKind.None);
        public Queue<BitLockerVolumeEvidence> Inspections { get; } = new();
        public List<string> Events { get; } = new();
        public BitLockerNativeResult BackupResult { get; set; } = new(true, "backed up");
        public BitLockerNativeResult SuspendResult { get; set; } = new(true, "suspended");
        public BitLockerNativeResult ResumeResult { get; set; } = new(true, "resumed");

        public BitLockerVolumeEvidence InspectSystemVolume() =>
            Inspections.Count > 0 ? Inspections.Dequeue() : Volume;

        public DirectoryJoinEvidence InspectDirectoryJoin() => Join;

        public BitLockerNativeResult BackupToActiveDirectory(string protectorId)
        {
            Events.Add("ad:" + protectorId);
            return BackupResult;
        }

        public BitLockerNativeResult BackupToMicrosoftEntra(string mountPoint, string protectorId)
        {
            Events.Add($"entra:{mountPoint}:{protectorId}");
            return BackupResult;
        }

        public BitLockerNativeResult SuspendSystemVolumeForOneReboot()
        {
            Events.Add("suspend");
            return SuspendResult;
        }

        public BitLockerNativeResult ResumeSystemVolume()
        {
            Events.Add("resume");
            return ResumeResult;
        }
    }
}
