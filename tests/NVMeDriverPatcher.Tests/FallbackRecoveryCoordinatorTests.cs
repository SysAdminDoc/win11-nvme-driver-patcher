using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class FallbackRecoveryCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"NVMeDriverPatcher.Recovery.Tests.{Guid.NewGuid():N}");

    public FallbackRecoveryCoordinatorTests() => Directory.CreateDirectory(_dir);

    private AppConfig ConfigWithFile() => new()
    {
        WorkingDir = _dir,
        ConfigFile = Path.Combine(_dir, "config.json")
    };

    private static VerificationReport NotBound() =>
        new() { Outcome = VerificationOutcome.FlagsEnabledNotBound };

    private static FeatureStoreWriteResult Ok() => new() { Success = true, Summary = "reset ok" };
    private static FeatureStoreWriteResult Fail() => new() { Success = false, Summary = "reset failed" };

    [Fact]
    public void RunOnce_NoFallbackApplied_DoesNothing()
    {
        var config = ConfigWithFile(); // PendingFallbackApplied defaults false
        bool resetCalled = false;
        var result = FallbackRecoveryCoordinator.RunOnce(config, NotBound(),
            () => { resetCalled = true; return Ok(); });

        Assert.False(result.Attempted);
        Assert.False(resetCalled);
    }

    [Fact]
    public void RunOnce_FallbackNotBound_ResetsClearsAndPersists()
    {
        var config = ConfigWithFile();
        config.PendingFallbackApplied = true;

        var result = FallbackRecoveryCoordinator.RunOnce(config, NotBound(), Ok);

        Assert.True(result.Attempted);
        Assert.True(result.Success);
        Assert.False(config.PendingFallbackApplied);

        // Durable: a fresh Load must NOT see the checkpoint.
        var reloaded = LoadFrom(config.ConfigFile);
        Assert.False(reloaded.PendingFallbackApplied);
    }

    [Fact]
    public void RunOnce_ResetFails_RetainsCheckpointForRetry()
    {
        var config = ConfigWithFile();
        config.PendingFallbackApplied = true;

        var result = FallbackRecoveryCoordinator.RunOnce(config, NotBound(), Fail);

        Assert.True(result.Attempted);
        Assert.False(result.Success);
        Assert.True(config.PendingFallbackApplied); // retained so the next startup retries
    }

    [Fact]
    public void RunOnce_IsIdempotent_AfterSuccess()
    {
        var config = ConfigWithFile();
        config.PendingFallbackApplied = true;

        FallbackRecoveryCoordinator.RunOnce(config, NotBound(), Ok);

        int resetCalls = 0;
        var second = FallbackRecoveryCoordinator.RunOnce(config, NotBound(),
            () => { resetCalls++; return Ok(); });

        Assert.False(second.Attempted);
        Assert.Equal(0, resetCalls); // cannot be re-triggered
    }

    [Fact]
    public void RunOnce_SaveFails_ReportsNotDurable()
    {
        // No ConfigFile → Save returns false, so a successful reset is reported as not durable.
        var config = new AppConfig { PendingFallbackApplied = true };

        var result = FallbackRecoveryCoordinator.RunOnce(config, NotBound(), Ok);

        Assert.True(result.Attempted);
        Assert.False(result.Success);
        Assert.Contains("persist", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // --- Persistence round-trip for the checkpoint field itself ---

    [Fact]
    public void PendingFallbackApplied_SurvivesSaveLoadRoundTrip()
    {
        var config = ConfigWithFile();
        config.PendingFallbackApplied = true;
        Assert.True(ConfigService.Save(config));

        var reloaded = LoadFrom(config.ConfigFile);
        Assert.True(reloaded.PendingFallbackApplied);
    }

    [Fact]
    public void Save_ReturnsFalse_WhenNoConfigFile()
    {
        Assert.False(ConfigService.Save(new AppConfig()));
    }

    private static AppConfig LoadFrom(string configFile)
    {
        // ConfigService.Load resolves the working dir itself, so read + deserialize the exact file.
        var json = File.ReadAllText(configFile);
        var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        // Saved config serializes enums (PatchProfile/ThemeMode) as strings.
        opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json, opts)!;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
