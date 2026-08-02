using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class DataServiceFailureLoggingTests : IDisposable
{
    private readonly string _workingDir = Path.Combine(
        Path.GetTempPath(),
        $"NVMePatcher_DataFailure_{Guid.NewGuid():N}");

    public DataServiceFailureLoggingTests()
    {
        Directory.CreateDirectory(_workingDir);
    }

    [Fact]
    public void NonStructuralFailure_IsWrittenAsOneReleaseSafeDiagnosticLine()
    {
        DataService.RecordStructuralFailure(
            "Saving benchmark history",
            new IOException("disk full\r\nretry later"),
            _workingDir);

        var logPath = Path.Combine(_workingDir, "diagnostics.log");
        var entries = File.ReadAllLines(logPath);

        var entry = Assert.Single(entries);
        Assert.Contains("[DataService] Saving benchmark history failed: IOException: disk full retry later", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", entry, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workingDir))
                Directory.Delete(_workingDir, recursive: true);
        }
        catch { }
    }
}
