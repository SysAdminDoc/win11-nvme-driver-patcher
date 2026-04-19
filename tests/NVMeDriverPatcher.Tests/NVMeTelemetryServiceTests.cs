using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class NVMeTelemetryServiceTests
{
    [Fact]
    public void Read128BitLE_ReturnsZeroForMissingData()
    {
        Assert.Equal(0m, NVMeTelemetryService.Read128BitLE(null));
        Assert.Equal(0m, NVMeTelemetryService.Read128BitLE([1, 2, 3]));
    }

    [Fact]
    public void Read128BitLE_ParsesLittleEndianCounters()
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(5UL + (1UL << 32)).CopyTo(bytes, 0);

        Assert.Equal(4_294_967_301m, NVMeTelemetryService.Read128BitLE(bytes));
    }

    [Fact]
    public void Read128BitLE_ClampsValuesBeyondDecimalRange()
    {
        var bytes = Enumerable.Repeat((byte)0xFF, 16).ToArray();

        Assert.Equal(decimal.MaxValue, NVMeTelemetryService.Read128BitLE(bytes));
    }
}
