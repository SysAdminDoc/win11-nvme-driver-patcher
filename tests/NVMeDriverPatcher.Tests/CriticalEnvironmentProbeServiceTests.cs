using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class CriticalEnvironmentProbeServiceTests
{
    private static readonly DateTimeOffset Observed = new(2026, 7, 14, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void Evaluate_ConfirmedSafeRegistryEnvironment_PassesWithTimestampedEvidence()
    {
        var report = Evaluate(new FakePlatform());

        Assert.True(report.AllPassed);
        Assert.False(report.HasUnknown);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(5, report.Items.Count);
        Assert.All(report.Items, item =>
        {
            Assert.Equal(CriticalProbeVerdict.Pass, item.Verdict);
            Assert.Equal(Observed, item.ObservedAtUtc);
            Assert.NotEmpty(item.Evidence);
            Assert.False(string.IsNullOrWhiteSpace(item.Detail));
        });
    }

    [Fact]
    public void Evaluate_FeatureStoreScope_OmitsSafeBootOnly()
    {
        var report = CriticalEnvironmentProbeService.Evaluate(
            new FakePlatform { SafeBootException = new UnauthorizedAccessException() },
            MutationProbeScope.FeatureStoreFallback,
            Observed);

        Assert.True(report.AllPassed);
        Assert.Equal(4, report.Items.Count);
        Assert.DoesNotContain(report.Items, item => item.Id == "SafeBoot");
    }

    [Fact]
    public void Evaluate_VeraCryptBootEvidence_IsConfirmedBlocker()
    {
        var platform = new FakePlatform
        {
            VeraCrypt = new VeraCryptProbeSnapshot(true, 0, true)
        };

        var probe = Evaluate(platform).Items.Single(item => item.Id == "VeraCrypt");

        Assert.Equal(CriticalProbeVerdict.Fail, probe.Verdict);
        Assert.Equal(CriticalProbeReasonCode.ConfirmedPresent, probe.ReasonCode);
        Assert.True(probe.BlocksMutation);
    }

    [Fact]
    public void Evaluate_VeraCryptServiceWithInvalidStart_IsUnknown()
    {
        var platform = new FakePlatform
        {
            VeraCrypt = new VeraCryptProbeSnapshot(true, null, false)
        };

        var report = Evaluate(platform);
        var probe = report.Items.Single(item => item.Id == "VeraCrypt");

        Assert.Equal(CriticalProbeVerdict.Unknown, probe.Verdict);
        Assert.Equal(CriticalProbeReasonCode.InvalidEvidence, probe.ReasonCode);
        Assert.Equal(2, report.ExitCode);
    }

    [Theory]
    [InlineData("iaStorAC")]
    [InlineData("IASTORVD")]
    [InlineData("vmd")]
    [InlineData("vmd_bus")]
    public void Evaluate_IntelStorageDriver_IsConfirmedBlocker(string driverName)
    {
        var platform = new FakePlatform
        {
            Drivers = [new StorageDriverProbeSnapshot(driverName, "Running", "Boot")]
        };

        var probe = Evaluate(platform).Items.Single(item => item.Id == "IntelStorage");

        Assert.Equal(CriticalProbeVerdict.Fail, probe.Verdict);
        Assert.Equal(CriticalProbeReasonCode.ConfirmedPresent, probe.ReasonCode);
        Assert.Contains(driverName, probe.Evidence[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_NoIntelStorageDriver_DistinguishesDeviceAbsent()
    {
        var probe = Evaluate(new FakePlatform()).Items.Single(item => item.Id == "IntelStorage");

        Assert.Equal(CriticalProbeVerdict.Pass, probe.Verdict);
        Assert.Equal(CriticalProbeReasonCode.DeviceAbsent, probe.ReasonCode);
    }

    [Fact]
    public void Evaluate_DisabledIntelStorageDriver_DistinguishesConfirmedDisabled()
    {
        var platform = new FakePlatform
        {
            Drivers = [new StorageDriverProbeSnapshot("iaStorVD", "Stopped", "Disabled")]
        };

        var probe = Evaluate(platform).Items.Single(item => item.Id == "IntelStorage");

        Assert.Equal(CriticalProbeVerdict.Pass, probe.Verdict);
        Assert.Equal(CriticalProbeReasonCode.ConfirmedDisabled, probe.ReasonCode);
    }

    [Fact]
    public void Evaluate_AccessDenied_DoesNotCollapseToNotDetected()
    {
        var report = Evaluate(new FakePlatform
        {
            VeraCryptException = new UnauthorizedAccessException("denied")
        });
        var probe = report.Items.Single(item => item.Id == "VeraCrypt");

        Assert.Equal(CriticalProbeVerdict.Unknown, probe.Verdict);
        Assert.Equal(CriticalProbeReasonCode.AccessDenied, probe.ReasonCode);
        Assert.NotNull(probe.NativeError);
        Assert.Equal(2, report.ExitCode);
    }

    [Fact]
    public void Evaluate_Timeout_DoesNotCollapseToDeviceAbsent()
    {
        var probe = Evaluate(new FakePlatform
        {
            DriverException = new TimeoutException("WMI timed out")
        }).Items.Single(item => item.Id == "IntelStorage");

        Assert.Equal(CriticalProbeVerdict.Unknown, probe.Verdict);
        Assert.Equal(CriticalProbeReasonCode.Timeout, probe.ReasonCode);
    }

    [Fact]
    public void Evaluate_UnsupportedApi_HasDistinctReasonCode()
    {
        var probe = Evaluate(new FakePlatform
        {
            DriverException = new PlatformNotSupportedException("WMI unavailable")
        }).Items.Single(item => item.Id == "IntelStorage");

        Assert.Equal(CriticalProbeVerdict.Unknown, probe.Verdict);
        Assert.Equal(CriticalProbeReasonCode.UnsupportedApi, probe.ReasonCode);
    }

    [Fact]
    public void Evaluate_MissingBitLockerProtector_IsConfirmedFailure()
    {
        var platform = new FakePlatform
        {
            BitLocker = EncryptedBitLocker(ids: [])
        };

        var report = Evaluate(platform);
        var probe = report.Items.Single(item => item.Id == "BitLocker");

        Assert.Equal(CriticalProbeVerdict.Fail, probe.Verdict);
        Assert.Equal(CriticalProbeReasonCode.RecoveryProtectorMissing, probe.ReasonCode);
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public void Evaluate_BitLockerProbeFailure_IsUnknownWithNativeError()
    {
        var platform = new FakePlatform
        {
            BitLocker = new BitLockerRecoveryProof(
                new BitLockerVolumeEvidence
                {
                    ProbeSucceeded = false,
                    MountPoint = "C:",
                    FailureCode = "AccessDenied",
                    NativeError = 5
                },
                new DirectoryJoinEvidence(true, DirectoryJoinKind.None))
        };

        var probe = Evaluate(platform).Items.Single(item => item.Id == "BitLocker");

        Assert.Equal(CriticalProbeVerdict.Unknown, probe.Verdict);
        Assert.Equal(CriticalProbeReasonCode.AccessDenied, probe.ReasonCode);
        Assert.Contains("0x00000005", probe.NativeError);
    }

    [Fact]
    public void Evaluate_SafeBootAccessDenied_IsConfirmedFailure()
    {
        var platform = new FakePlatform
        {
            SafeBoot = (SafeBootKeyDisposition.AccessDenied, SafeBootKeyDisposition.WritableAbsent)
        };

        var probe = Evaluate(platform).Items.Single(item => item.Id == "SafeBoot");

        Assert.Equal(CriticalProbeVerdict.Fail, probe.Verdict);
        Assert.Equal(CriticalProbeReasonCode.AccessDenied, probe.ReasonCode);
    }

    [Theory]
    [InlineData("stornvme", false)]
    [InlineData("iaStorAVC", true)]
    [InlineData("vmd_bus", true)]
    [InlineData("iaLPSS2_I2C", false)]
    [InlineData("not-vmd", false)]
    public void IntelStorageNameClassifier_IsAnchored(string name, bool expected)
    {
        Assert.Equal(expected, WindowsCriticalProbePlatform.IsBlockingIntelStorageDriver(name));
    }

    private static CriticalProbeReport Evaluate(FakePlatform platform) =>
        CriticalEnvironmentProbeService.Evaluate(platform, MutationProbeScope.RegistryPatch, Observed);

    private static BitLockerRecoveryProof EncryptedBitLocker(IReadOnlyList<string> ids) => new(
        new BitLockerVolumeEvidence
        {
            ProbeSucceeded = true,
            SystemVolumePresent = true,
            MountPoint = "C:",
            ConversionStatus = 1,
            ProtectionStatus = 1,
            RecoveryProtectorIds = ids
        },
        new DirectoryJoinEvidence(true, DirectoryJoinKind.None));

    private sealed class FakePlatform : ICriticalEnvironmentProbePlatform
    {
        public bool Administrator { get; set; } = true;
        public VeraCryptProbeSnapshot VeraCrypt { get; set; } = new(false, null, false);
        public IReadOnlyList<StorageDriverProbeSnapshot> Drivers { get; set; } = [];
        public BitLockerRecoveryProof BitLocker { get; set; } = new(
            new BitLockerVolumeEvidence
            {
                ProbeSucceeded = true,
                SystemVolumePresent = true,
                MountPoint = "C:",
                ConversionStatus = 0,
                ProtectionStatus = 0,
                RecoveryProtectorIds = []
            },
            new DirectoryJoinEvidence(false, DirectoryJoinKind.None, "not needed"));
        public (SafeBootKeyDisposition Minimal, SafeBootKeyDisposition Network) SafeBoot { get; set; } =
            (SafeBootKeyDisposition.WritableAbsent, SafeBootKeyDisposition.AlreadyCorrect);

        public Exception? AdministratorException { get; set; }
        public Exception? VeraCryptException { get; set; }
        public Exception? DriverException { get; set; }
        public Exception? BitLockerException { get; set; }
        public Exception? SafeBootException { get; set; }

        public bool IsAdministrator()
        {
            if (AdministratorException is not null) throw AdministratorException;
            return Administrator;
        }

        public VeraCryptProbeSnapshot InspectVeraCrypt()
        {
            if (VeraCryptException is not null) throw VeraCryptException;
            return VeraCrypt;
        }

        public IReadOnlyList<StorageDriverProbeSnapshot> InspectSystemDrivers()
        {
            if (DriverException is not null) throw DriverException;
            return Drivers;
        }

        public BitLockerRecoveryProof InspectBitLocker()
        {
            if (BitLockerException is not null) throw BitLockerException;
            return BitLocker;
        }

        public (SafeBootKeyDisposition Minimal, SafeBootKeyDisposition Network) InspectSafeBootKeys()
        {
            if (SafeBootException is not null) throw SafeBootException;
            return SafeBoot;
        }
    }
}
