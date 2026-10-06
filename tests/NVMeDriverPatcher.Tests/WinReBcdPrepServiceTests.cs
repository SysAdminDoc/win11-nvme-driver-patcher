using System.IO;
using System.Runtime.InteropServices;
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

    [Fact]
    public void ParseRecoverySettings_ReadsRemediationStatesWithoutReturningSensitivePayload()
    {
        const string stdout = """
            reagentc settings:
            <WindowsRE>
              <CloudRemediation state="1" />
              <AutoRemediation state="0" />
              <WifiProfile ssid="office" password="secret-that-must-not-be-returned" />
            </WindowsRE>
            """;

        var parsed = WinReBcdPrepService.ParseRecoverySettings(stdout);

        Assert.True(parsed.Parsed);
        Assert.True(parsed.CloudRemediationEnabled);
        Assert.False(parsed.AutoRemediationEnabled);
    }

    [Fact]
    public void ParseRecoverySettings_AcceptsBooleanStateAttributes()
    {
        const string stdout = "<WindowsRE><CloudRemediation state=\"true\" /><AutoRemediation state=\"false\" /></WindowsRE>";

        var parsed = WinReBcdPrepService.ParseRecoverySettings(stdout);

        Assert.True(parsed.Parsed);
        Assert.True(parsed.CloudRemediationEnabled);
        Assert.False(parsed.AutoRemediationEnabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<WindowsRE>")]
    public void ParseRecoverySettings_RejectsMissingOrMalformedXml(string? stdout)
    {
        var parsed = WinReBcdPrepService.ParseRecoverySettings(stdout);

        Assert.False(parsed.Parsed);
        Assert.Null(parsed.CloudRemediationEnabled);
        Assert.Null(parsed.AutoRemediationEnabled);
    }

    // --- WinRE image path: what `bcdedit /enum {guid} /v` prints for the recovery entry ---

    private const string Location = @"\\?\GLOBALROOT\device\harddisk0\partition4\Recovery\WindowsRE";
    private const string FromBcd = @"\\?\GLOBALROOT\Device\HarddiskVolume4\Recovery\WindowsRE\Winre.wim";
    private const string FromLocation = Location + @"\Winre.wim";

    private static string RecoveryEntry(string volume) =>
        "Windows Boot Loader\r\n" +
        "-------------------\r\n" +
        "identifier              {7d2c8f1a-3b4c-4d5e-8f90-1a2b3c4d5e6f}\r\n" +
        $"device                  ramdisk=[{volume}]\\Recovery\\WindowsRE\\Winre.wim,{{7d2c8f1b-3b4c-4d5e-8f90-1a2b3c4d5e6f}}\r\n" +
        "path                    \\windows\\system32\\winload.efi\r\n" +
        "description             Windows Recovery Environment\r\n" +
        "locale                  en-US\r\n" +
        "inherit                 {bootloadersettings}\r\n" +
        "displaymessage          Recovery\r\n" +
        $"osdevice                ramdisk=[{volume}]\\Recovery\\WindowsRE\\Winre.wim,{{7d2c8f1b-3b4c-4d5e-8f90-1a2b3c4d5e6f}}\r\n" +
        "systemroot              \\windows\r\n" +
        "nx                      OptIn\r\n" +
        "bootmenupolicy          Standard\r\n" +
        "winpe                   Yes\r\n";

    [Theory]
    [InlineData(@"\Device\HarddiskVolume4", FromBcd)]
    [InlineData(@"\device\harddiskvolume12", @"\\?\GLOBALROOT\device\harddiskvolume12\Recovery\WindowsRE\Winre.wim")]
    [InlineData("C:", @"C:\Recovery\WindowsRE\Winre.wim")]
    [InlineData(@"\\?\Volume{0f3a1c52-6c1e-4f0a-9d3b-2a7e5b8c9d10}", @"\\?\Volume{0f3a1c52-6c1e-4f0a-9d3b-2a7e5b8c9d10}\Recovery\WindowsRE\Winre.wim")]
    public void ParseBcdImagePath_GivesAPathWin32CanOpen(string volume, string expected) =>
        Assert.Equal(expected, WinReBcdPrepService.ParseBcdImagePath(RecoveryEntry(volume)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("The boot configuration data store could not be opened.\r\nAccess is denied.\r\n")]
    public void ParseBcdImagePath_NoRecoveryEntry_IsNull(string? stdout) =>
        Assert.Null(WinReBcdPrepService.ParseBcdImagePath(stdout));

    [Fact]
    public void ParseBcdImagePath_VolumeBcdCouldNotResolve_IsNull() =>
        Assert.Null(WinReBcdPrepService.ParseBcdImagePath(RecoveryEntry("unknown")));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string lpDeviceName, [Out] char[] lpTargetPath, uint ucchMax);

    [Fact]
    public void ParseBcdImagePath_NtDeviceVolume_OpensWhereTheGluedPathDidNot()
    {
        // The real thing, against this machine's own system volume. bcdedit names a volume with no
        // drive letter as \Device\HarddiskVolumeN, and the recovery partition never has a letter.
        // Gluing the file path onto that gave a path no Win32 call can open, so `winre-inject`
        // reported the image missing on every standard install.
        var system = Environment.SystemDirectory;
        var buffer = new char[512];
        Assert.True(QueryDosDeviceW(system[..2], buffer, (uint)buffer.Length) > 0);
        var device = new string(buffer, 0, Array.IndexOf(buffer, '\0'));
        var file = system[2..] + @"\kernel32.dll";

        var resolved = WinReBcdPrepService.ParseBcdImagePath(
            $"osdevice                ramdisk=[{device}]{file},{{7d2c8f1b-3b4c-4d5e-8f90-1a2b3c4d5e6f}}\r\n");

        Assert.Equal(@"\\?\GLOBALROOT" + device + file, resolved);
        Assert.True(File.Exists(resolved));
        Assert.False(File.Exists(device + file));
    }

    [Fact]
    public void ResolveImagePath_PrefersTheImageTheBootEntryNames() =>
        Assert.Equal(FromBcd, WinReBcdPrepService.ResolveImagePath(
            RecoveryEntry(@"\Device\HarddiskVolume4"), Location, _ => true));

    [Fact]
    public void ResolveImagePath_BootEntryPathNotOnDisk_UsesTheReagentcLocation() =>
        Assert.Equal(FromLocation, WinReBcdPrepService.ResolveImagePath(
            RecoveryEntry(@"\Device\HarddiskVolume4"), Location + @"\", p => p == FromLocation));

    [Fact]
    public void ResolveImagePath_BcdGaveNoPath_UsesTheReagentcLocation() =>
        Assert.Equal(FromLocation, WinReBcdPrepService.ResolveImagePath(
            RecoveryEntry("unknown"), Location, _ => false));

    [Fact]
    public void ResolveImagePath_NothingOnDisk_NamesTheBootEntrysPath() =>
        Assert.Equal(FromBcd, WinReBcdPrepService.ResolveImagePath(
            RecoveryEntry(@"\Device\HarddiskVolume4"), Location, _ => false));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "   ")]
    public void ResolveImagePath_NothingKnown_IsNull(string? bcd, string? location) =>
        Assert.Null(WinReBcdPrepService.ResolveImagePath(bcd, location, _ => true));
}
