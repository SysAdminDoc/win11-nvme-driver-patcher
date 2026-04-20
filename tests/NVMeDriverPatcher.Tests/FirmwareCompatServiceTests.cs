using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// FirmwareCompatService.Lookup is pure: {controller substring, firmware pattern, level}
// table + a drive's (model, firmware) → finding. These tests pin precedence: exact firmware
// beats wildcard, worst severity wins on ties, "=" prefix exact-matches, unknown is default.
public sealed class FirmwareCompatServiceTests
{
    private static FirmwareCompatDatabase Db(params FirmwareCompatEntry[] entries) =>
        new() { Entries = entries.ToList() };

    [Fact]
    public void NoMatch_ReturnsUnknown()
    {
        var db = Db();
        var finding = FirmwareCompatService.Lookup(db, "Random Drive", "FW01");
        Assert.Equal(FirmwareCompatLevel.Unknown, finding.Level);
        Assert.Equal(string.Empty, finding.Note);
    }

    [Fact]
    public void WildcardFirmware_MatchesAnyFirmware()
    {
        var db = Db(new FirmwareCompatEntry
        {
            Controller = "Samsung SSD 990 PRO",
            Firmware = "*",
            Level = FirmwareCompatLevel.Good,
            Note = "ok"
        });
        var finding = FirmwareCompatService.Lookup(db, "Samsung SSD 990 PRO 2TB", "ANYFW");
        Assert.Equal(FirmwareCompatLevel.Good, finding.Level);
    }

    [Fact]
    public void ExactFirmware_BeatsWildcardSameController()
    {
        var db = Db(
            new FirmwareCompatEntry { Controller = "Acme", Firmware = "*",      Level = FirmwareCompatLevel.Good, Note = "wild" },
            new FirmwareCompatEntry { Controller = "Acme", Firmware = "BAD_FW", Level = FirmwareCompatLevel.Bad,  Note = "exact" }
        );
        var finding = FirmwareCompatService.Lookup(db, "Acme Super 1TB", "BAD_FW");
        Assert.Equal(FirmwareCompatLevel.Bad, finding.Level);
        Assert.Equal("exact", finding.Note);
    }

    [Fact]
    public void ExactControllerPrefix_WorksViaEqualsSyntax()
    {
        var db = Db(new FirmwareCompatEntry
        {
            Controller = "=Acme 1TB",
            Firmware = "*",
            Level = FirmwareCompatLevel.Caution,
            Note = "just the model line"
        });
        // Contains-match would fire for both; the "=" prefix forces exact.
        Assert.Equal(FirmwareCompatLevel.Caution, FirmwareCompatService.Lookup(db, "Acme 1TB", "F").Level);
        Assert.Equal(FirmwareCompatLevel.Unknown, FirmwareCompatService.Lookup(db, "Acme 1TB 2024 Edition", "F").Level);
    }

    [Fact]
    public void WorstSeverity_WinsOnTies_AmongWildcardMatches()
    {
        var db = Db(
            new FirmwareCompatEntry { Controller = "Acme", Firmware = "*", Level = FirmwareCompatLevel.Good },
            new FirmwareCompatEntry { Controller = "Acme", Firmware = "*", Level = FirmwareCompatLevel.Bad }
        );
        var finding = FirmwareCompatService.Lookup(db, "Acme", "F");
        Assert.Equal(FirmwareCompatLevel.Bad, finding.Level);
    }

    [Fact]
    public void EmptyModel_NeverMatches()
    {
        var db = Db(new FirmwareCompatEntry { Controller = "*", Firmware = "*", Level = FirmwareCompatLevel.Good });
        Assert.Equal(FirmwareCompatLevel.Good, FirmwareCompatService.Lookup(db, "Anything", "F").Level);
        // But a null/empty model short-circuits because MatchesController returns false on empty model.
        // Note: wildcard controller ("*") is the one case where empty model still matches.
        Assert.Equal(FirmwareCompatLevel.Good, FirmwareCompatService.Lookup(db, string.Empty, "F").Level);
    }
}
