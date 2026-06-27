using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class PortableModeServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NVMePatcher_PortableTests_" + Guid.NewGuid().ToString("N"));

    public PortableModeServiceTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void IsPortable_ReturnsFalseWithoutFlag()
    {
        Assert.False(PortableModeService.IsPortable(_dir));
    }

    [Fact]
    public void EnableDisable_TogglesPortableFlag()
    {
        var messages = new List<string>();

        Assert.True(PortableModeService.Enable(_dir, messages.Add));
        Assert.True(PortableModeService.IsPortable(_dir));
        Assert.Contains(messages, m => m.Contains("Portable mode enabled", StringComparison.OrdinalIgnoreCase));

        Assert.True(PortableModeService.Disable(_dir, messages.Add));
        Assert.False(PortableModeService.IsPortable(_dir));
        Assert.Contains(messages, m => m.Contains("Portable mode disabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PortableDataPath_CreatesDataDirectory()
    {
        var path = PortableModeService.PortableDataPath(_dir);

        Assert.Equal(Path.Combine(_dir, "Data"), path);
        Assert.True(Directory.Exists(path));
    }
}
