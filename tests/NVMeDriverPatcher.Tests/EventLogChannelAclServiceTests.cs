using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class EventLogChannelAclServiceTests
{
    private const string SystemChannelSddl =
        "O:BAG:SYD:(A;;0xf0007;;;SY)(A;;0x7;;;BA)(A;;0x3;;;BO)(A;;0x5;;;SO)(A;;0x1;;;IU)(A;;0x3;;;SU)(A;;0x1;;;S-1-5-3)(A;;0x2;;;S-1-5-33)(A;;0x1;;;S-1-5-32-573)";

    [Fact]
    public void EnsureLocalServiceReadAce_AddsReadAceWhenMissing()
    {
        var updated = EventLogChannelAclService.EnsureLocalServiceReadAce(SystemChannelSddl, out var changed);

        Assert.True(changed);
        Assert.True(EventLogChannelAclService.HasLocalServiceReadAce(updated));
        Assert.Contains("SY", updated, StringComparison.Ordinal);
        Assert.Contains("BA", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureLocalServiceReadAce_IsIdempotentWhenReadAceExists()
    {
        var first = EventLogChannelAclService.EnsureLocalServiceReadAce(SystemChannelSddl, out var firstChanged);
        var second = EventLogChannelAclService.EnsureLocalServiceReadAce(first, out var secondChanged);

        Assert.True(firstChanged);
        Assert.False(secondChanged);
        Assert.Equal(first, second);
    }

    [Fact]
    public void HasLocalServiceReadAce_RejectsMalformedSddl()
    {
        Assert.False(EventLogChannelAclService.HasLocalServiceReadAce("not sddl"));
    }
}
