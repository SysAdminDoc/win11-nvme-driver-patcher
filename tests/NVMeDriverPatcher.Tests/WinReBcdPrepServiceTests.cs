using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// WinReBcdPrepService.Probe shells out to reagentc/bcdedit — can't be fully tested without
// root. What we CAN pin is the Summary ladder and the shape of the returned object for
// synthetic inputs. This test file is intentionally small: asserts the service emits
// stable English strings + always returns a non-null WinReProvisionInfo.
public sealed class WinReBcdPrepServiceTests
{
    [Fact]
    public void Probe_AlwaysReturnsNonNullReport()
    {
        // Even when reagentc isn't available (test runner may be non-admin), the service
        // must return a populated report — never throw, never null.
        var info = WinReBcdPrepService.Probe();
        Assert.NotNull(info);
        Assert.False(string.IsNullOrWhiteSpace(info.Summary));
    }

    // Locale-independent parse: the GUID + device path are structural, the labels are translated.
    // A non-zero BCD identifier GUID means enabled regardless of UI language.

    private const string EnUsEnabled =
        "Windows Recovery Environment (Windows RE) and system reset configuration\r\n" +
        "Information:\r\n\r\n" +
        "    Windows RE status:         Enabled\r\n" +
        "    Windows RE location:       \\\\?\\GLOBALROOT\\device\\harddisk0\\partition4\\Recovery\\WindowsRE\r\n" +
        "    Boot Configuration Data (BCD) identifier: 7d2c8f1a-3b4c-4d5e-8f90-1a2b3c4d5e6f\r\n";

    private const string DeDeEnabled =
        "Windows-Wiederherstellungsumgebung (Windows RE) und Konfiguration zum Zurücksetzen des Systems\r\n" +
        "Informationen:\r\n\r\n" +
        "    Windows RE-Status:          Aktiviert\r\n" +
        "    Windows RE-Speicherort:     \\\\?\\GLOBALROOT\\device\\harddisk0\\partition4\\Recovery\\WindowsRE\r\n" +
        "    Bezeichner für Startkonfigurationsdaten (BCD): {7d2c8f1a-3b4c-4d5e-8f90-1a2b3c4d5e6f}\r\n";

    private const string JaJpEnabled =
        "Windows 回復環境 (Windows RE) およびシステム リセット構成\r\n" +
        "情報:\r\n\r\n" +
        "    Windows RE の状態:          有効\r\n" +
        "    Windows RE の場所:          \\\\?\\GLOBALROOT\\device\\harddisk0\\partition4\\Recovery\\WindowsRE\r\n" +
        "    ブート構成データ (BCD) 識別子: 7d2c8f1a-3b4c-4d5e-8f90-1a2b3c4d5e6f\r\n";

    private const string DisabledZeroGuid =
        "    Windows RE status:         Disabled\r\n" +
        "    Windows RE location:       \r\n" +
        "    Boot Configuration Data (BCD) identifier: 00000000-0000-0000-0000-000000000000\r\n";

    [Theory]
    [InlineData(EnUsEnabled)]
    [InlineData(DeDeEnabled)]
    [InlineData(JaJpEnabled)]
    public void ParseReagentcInfo_DetectsEnabled_AcrossLocales(string stdout)
    {
        var (enabled, location, guid) = WinReBcdPrepService.ParseReagentcInfo(stdout);
        Assert.True(enabled);
        Assert.Equal("{7d2c8f1a-3b4c-4d5e-8f90-1a2b3c4d5e6f}", guid);
        Assert.NotNull(location);
        Assert.Contains("Recovery\\WindowsRE", location!);
    }

    [Theory]
    [InlineData(DisabledZeroGuid)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseReagentcInfo_ReportsDisabled_ForZeroGuidOrEmpty(string stdout)
    {
        var (enabled, _, guid) = WinReBcdPrepService.ParseReagentcInfo(stdout);
        Assert.False(enabled);
        Assert.Null(guid);
    }

    [Fact]
    public void ParseReagentcInfo_IgnoresGuidOutsideBcdIdentifierRow()
    {
        const string expected = "7d2c8f1a-3b4c-4d5e-8f90-1a2b3c4d5e6f";
        const string unrelated = "11111111-2222-3333-4444-555555555555";
        var stdout =
            $"Recovery package identifier: {unrelated}\r\n" +
            $"Boot Configuration Data (BCD) identifier: {expected}\r\n";

        var (enabled, _, guid) = WinReBcdPrepService.ParseReagentcInfo(stdout);

        Assert.True(enabled);
        Assert.Equal($"{{{expected}}}", guid);
    }

    [Fact]
    public void ParseReagentcInfo_DoesNotTreatUnrelatedGuidAsEnabled()
    {
        const string stdout =
            "Recovery package identifier: 11111111-2222-3333-4444-555555555555\r\n" +
            "Boot Configuration Data (BCD) identifier: 00000000-0000-0000-0000-000000000000\r\n";

        var (enabled, _, guid) = WinReBcdPrepService.ParseReagentcInfo(stdout);

        Assert.False(enabled);
        Assert.Null(guid);
    }
}
