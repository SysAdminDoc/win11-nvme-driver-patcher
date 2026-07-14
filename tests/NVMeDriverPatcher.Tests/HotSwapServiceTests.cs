using System.Diagnostics;
using System.Runtime.InteropServices;
using NVMeDriverPatcher.Interop;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class HotSwapServiceTests
{
    [Fact]
    public void CanHotSwap_AllowsNonBootNvmeWithDeviceId()
    {
        var drive = new SystemDrive
        {
            IsNVMe = true,
            IsBoot = false,
            PNPDeviceID = @"SCSI\DISK&VEN_NVME&PROD_TEST"
        };

        Assert.True(HotSwapService.CanHotSwap(drive));
    }

    [Theory]
    [InlineData(true, false, @"SCSI\DISK&VEN_NVME&PROD_TEST")]
    [InlineData(false, false, @"SCSI\DISK&VEN_NVME&PROD_TEST")]
    [InlineData(false, true, "")]
    [InlineData(false, true, "Unknown")]
    public void CanHotSwap_BlocksUnsafeOrUnknownTargets(bool isBoot, bool isNvme, string pnpDeviceId)
    {
        var drive = new SystemDrive
        {
            IsNVMe = isNvme,
            IsBoot = isBoot,
            PNPDeviceID = pnpDeviceId
        };

        Assert.False(HotSwapService.CanHotSwap(drive));
    }

    [Theory]
    [InlineData("C:")]
    [InlineData("z:")]
    public void IsSimpleDriveLetter_AllowsOnlyBareDriveLetters(string driveLetter)
    {
        Assert.True(HotSwapService.IsSimpleDriveLetter(driveLetter));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("C")]
    [InlineData("C:\\")]
    [InlineData("C: /P")]
    [InlineData("1:")]
    [InlineData("AA:")]
    public void IsSimpleDriveLetter_RejectsMalformedOrUnsafeValues(string? driveLetter)
    {
        Assert.False(HotSwapService.IsSimpleDriveLetter(driveLetter));
    }

    [Fact]
    public async Task SwapAsync_SucceedsOnlyAfterDriverAndEveryVolumeAreProved()
    {
        var platform = new FakePlatform();

        var result = await HotSwapService.SwapAsync(CreateDrive(), platform);

        Assert.True(result.Success);
        Assert.Equal(HotSwapOutcome.Succeeded, result.Outcome);
        Assert.True(result.DeviceReturned);
        Assert.True(result.DriverProofVerified);
        Assert.True(result.VolumeRestoreVerified);
        Assert.False(result.RebootRequired);
        Assert.Equal("stornvme.sys", result.ActiveDriver);
        Assert.Equal(new[] { "D:", "E:" }, result.CapturedVolumeLetters);
        Assert.Equal(new[]
        {
            "capture", "resolve", "bitlocker", "flush:D:", "flush:E:",
            "dismount:D:", "dismount:E:", "state", "present", "proof", "remount"
        }, platform.Operations);
        var successEvent = Assert.Single(platform.Events);
        Assert.Equal(EventLogEntryType.Information, successEvent.Type);
    }

    [Fact]
    public async Task SwapAsync_FlushFailureAbortsBeforeAnyDismountOrStateChange()
    {
        var platform = new FakePlatform { FlushFailureLetter = "E:" };

        var result = await HotSwapService.SwapAsync(CreateDrive(), platform);

        Assert.False(result.Success);
        Assert.Equal(HotSwapOutcome.Failed, result.Outcome);
        Assert.True(result.VolumeRestoreVerified);
        Assert.Contains("FlushFileBuffers failed for E:", result.ErrorMessage);
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("dismount", StringComparison.Ordinal));
        Assert.DoesNotContain("state", platform.Operations);
        Assert.Empty(platform.Events);
    }

    [Fact]
    public async Task SwapAsync_PartialDismountRestoresEarlierVolumeAndNeverChangesController()
    {
        var platform = new FakePlatform { DismountFailureLetter = "E:" };

        var result = await HotSwapService.SwapAsync(CreateDrive(), platform);

        Assert.False(result.Success);
        Assert.Equal(HotSwapOutcome.Failed, result.Outcome);
        Assert.True(result.VolumeRestoreVerified);
        Assert.False(result.RebootRequired);
        Assert.Contains("remount", platform.Operations);
        Assert.DoesNotContain("state", platform.Operations);
    }

    [Fact]
    public async Task SwapAsync_SetupApiRestartFlagCanNeverProduceSuccess()
    {
        var platform = new FakePlatform
        {
            StateChange = new HotSwapService.DeviceStateChange(true, true, 0, "DI_NEEDRESTART")
        };

        var result = await HotSwapService.SwapAsync(CreateDrive(), platform);

        Assert.False(result.Success);
        Assert.Equal(HotSwapOutcome.Partial, result.Outcome);
        Assert.True(result.RebootRequired);
        Assert.True(result.VolumeRestoreVerified);
        Assert.Contains("SetupAPI requested restart", result.ErrorMessage);
        Assert.DoesNotContain(platform.Events, item => item.Type == EventLogEntryType.Information);
    }

    [Fact]
    public async Task SwapAsync_MissingDriverServiceProofReturnsPartialAndRequiresReboot()
    {
        var platform = new FakePlatform
        {
            Proof = HotSwapService.DriverProof.Failed("service is not Running")
        };

        var result = await HotSwapService.SwapAsync(CreateDrive(), platform);

        Assert.False(result.Success);
        Assert.Equal(HotSwapOutcome.Partial, result.Outcome);
        Assert.False(result.DriverProofVerified);
        Assert.True(result.RebootRequired);
        Assert.True(result.VolumeRestoreVerified);
        Assert.Contains("service is not Running", result.ErrorMessage);
        Assert.DoesNotContain(platform.Events, item => item.Type == EventLogEntryType.Information);
    }

    [Fact]
    public async Task SwapAsync_UnrestoredVolumeReturnsPartialEvenWithHealthyDriver()
    {
        var platform = new FakePlatform { RemountFailureLetter = "E:" };

        var result = await HotSwapService.SwapAsync(CreateDrive(), platform);

        Assert.False(result.Success);
        Assert.Equal(HotSwapOutcome.Partial, result.Outcome);
        Assert.False(result.VolumeRestoreVerified);
        Assert.Equal(new[] { "E:" }, result.FailedRemountLetters);
        Assert.True(result.RebootRequired);
        Assert.Contains("unrestored volumes: E:", result.ErrorMessage);
        Assert.DoesNotContain(platform.Events, item => item.Type == EventLogEntryType.Information);
    }

    [Fact]
    public async Task SwapAsync_SetupApiFailureRestoresVolumesAndRequiresReboot()
    {
        var platform = new FakePlatform
        {
            StateChange = HotSwapService.DeviceStateChange.Failed("access denied", 5)
        };

        var result = await HotSwapService.SwapAsync(CreateDrive(), platform);

        Assert.False(result.Success);
        Assert.Equal(HotSwapOutcome.Failed, result.Outcome);
        Assert.Equal(5, result.NativeError);
        Assert.True(result.VolumeRestoreVerified);
        Assert.True(result.RebootRequired);
        Assert.Contains("Reboot required", result.ErrorMessage);
        Assert.DoesNotContain(platform.Events, item => item.Type == EventLogEntryType.Information);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(0x80u, true)]
    [InlineData(0x100u, true)]
    [InlineData(0x180u, true)]
    public void InstallFlagsRequireReboot_RecognizesBothSetupApiFlags(uint flags, bool expected)
    {
        Assert.Equal(expected, HotSwapService.InstallFlagsRequireReboot(flags));
    }

    [Fact]
    public void SetupApiInteropStructs_HaveNativeWindowsLayout()
    {
        Assert.Equal(IntPtr.Size == 8 ? 584 : 556, Marshal.SizeOf<NativeMethods.SP_DEVINSTALL_PARAMS>());
        Assert.Equal(20, Marshal.SizeOf<NativeMethods.SP_PROPCHANGE_PARAMS>());
        Assert.Equal((uint)Marshal.SizeOf<NativeMethods.SP_DEVINSTALL_PARAMS>(),
            NativeMethods.SP_DEVINSTALL_PARAMS.Create().cbSize);
    }

    private static SystemDrive CreateDrive() => new()
    {
        Number = 3,
        Name = "Test NVMe",
        IsNVMe = true,
        IsBoot = false,
        PNPDeviceID = @"SCSI\DISK&VEN_NVME&PROD_TEST"
    };

    private sealed class FakePlatform : HotSwapService.IHotSwapPlatform
    {
        private readonly List<HotSwapService.MountedVolume> _volumes =
        [
            new("D:", @"\\?\Volume{11111111-1111-1111-1111-111111111111}\"),
            new("E:", @"\\?\Volume{22222222-2222-2222-2222-222222222222}\")
        ];

        public string? FlushFailureLetter { get; init; }
        public string? DismountFailureLetter { get; init; }
        public string? RemountFailureLetter { get; init; }
        public bool DrivePresent { get; init; } = true;
        public HotSwapService.DeviceStateChange StateChange { get; init; } =
            new(true, false, 0, "changed");
        public HotSwapService.DriverProof Proof { get; init; } =
            new(true, "stornvme.sys", "stornvme", "Running", "verified");
        public List<string> Operations { get; } = [];
        public List<(string Message, EventLogEntryType Type, int Id)> Events { get; } = [];

        public HotSwapService.VolumeCaptureResult GetVolumesForDrive(int driveNumber)
        {
            Operations.Add("capture");
            return new HotSwapService.VolumeCaptureResult { Succeeded = true, Volumes = _volumes.ToList() };
        }

        public List<string> DescribeBitLockerRisk(List<HotSwapService.MountedVolume> volumes)
        {
            Operations.Add("bitlocker");
            return [];
        }

        public string? ResolveControllerDeviceId(string diskDeviceId)
        {
            Operations.Add("resolve");
            return @"PCI\VEN_1234&DEV_5678";
        }

        public HotSwapService.PlatformOperation FlushVolume(HotSwapService.MountedVolume volume)
        {
            Operations.Add($"flush:{volume.Letter}");
            return volume.Letter == FlushFailureLetter
                ? HotSwapService.PlatformOperation.Failed("simulated flush failure", 1117)
                : HotSwapService.PlatformOperation.Passed("flushed");
        }

        public HotSwapService.PlatformOperation DismountVolume(HotSwapService.MountedVolume volume)
        {
            Operations.Add($"dismount:{volume.Letter}");
            return volume.Letter == DismountFailureLetter
                ? HotSwapService.PlatformOperation.Failed("simulated dismount failure")
                : HotSwapService.PlatformOperation.Passed("dismounted");
        }

        public HotSwapService.DeviceStateChange RequestControllerStateChange(string controllerDeviceId)
        {
            Operations.Add("state");
            return StateChange;
        }

        public bool IsDrivePresent(int driveNumber)
        {
            Operations.Add("present");
            return DrivePresent;
        }

        public HotSwapService.DriverProof ProbeController(string controllerDeviceId)
        {
            Operations.Add("proof");
            return Proof;
        }

        public HotSwapService.RemountSummary RemountVolumes(
            List<HotSwapService.MountedVolume> volumes,
            Action<string>? log)
        {
            Operations.Add("remount");
            var summary = new HotSwapService.RemountSummary();
            foreach (var volume in volumes)
            {
                if (volume.Letter == RemountFailureLetter) summary.Failed.Add(volume.Letter);
                else summary.Restored.Add(volume.Letter);
            }
            return summary;
        }

        public void Delay(TimeSpan duration) { }

        public void WriteEvent(string message, EventLogEntryType entryType, int eventId) =>
            Events.Add((message, entryType, eventId));
    }
}
