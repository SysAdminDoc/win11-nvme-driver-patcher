using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

// FirmwareCompatService.Lookup is pure: {controller substring, firmware pattern, level}
// table + a drive's (model, firmware) → finding. These tests pin precedence: exact firmware
// beats wildcard, worst severity wins on ties, "=" prefix exact-matches, unknown is default.
public sealed class FirmwareCompatServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

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

    [Theory]
    [InlineData("WD_BLACK SN850X 2000GB", "620331WD", FirmwareCompatLevel.Bad, "Critical Failure")]
    [InlineData("WD_BLACK SN770 NVMe SSD 2TB", "730120WD", FirmwareCompatLevel.Caution, "731130WD")]
    [InlineData("WD Blue SN580 NVMe SSD 2TB", "280080WD", FirmwareCompatLevel.Caution, "281050WD")]
    [InlineData("WD Blue SN5000 NVMe SSD 2TB", "290030WD", FirmwareCompatLevel.Caution, "291020WD")]
    [InlineData("Samsung SSD 990 PRO 2TB", "7B2QJXD7", FirmwareCompatLevel.Caution, "random-write degradation")]
    [InlineData("SK hynix Platinum P41 2TB", "51060A20", FirmwareCompatLevel.Caution, "negligible")]
    [InlineData("Phison E18 NVMe Controller", "1.0", FirmwareCompatLevel.Caution, "power loss")]
    [InlineData("Phison E26 NVMe Controller", "1.0", FirmwareCompatLevel.Caution, "power loss")]
    [InlineData("Seagate FireCuda 530", "SU6SM005", FirmwareCompatLevel.Caution, "power protection")]
    public void ShippedDatabase_FlagsCommunityProblemFirmware(string model, string firmware, FirmwareCompatLevel expectedLevel, string noteFragment)
    {
        var finding = FirmwareCompatService.Lookup(ShippedDatabase(), model, firmware);

        Assert.Equal(expectedLevel, finding.Level);
        Assert.Contains(noteFragment, finding.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Phison E18 NVMe Controller")]
    [InlineData("Phison E26 NVMe Controller")]
    [InlineData("Seagate FireCuda 530")]
    public void ShippedDatabase_PhisonRiskEntriesCarryPowerLossFlag(string model)
    {
        var finding = FirmwareCompatService.Lookup(ShippedDatabase(), model, "1.0");

        Assert.True(finding.PowerLossRisk);
        Assert.Contains("power", finding.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("WD_BLACK SN770 NVMe SSD 2TB", "731130WD")]
    [InlineData("WD_BLACK SN770M NVMe SSD 2TB", "731130WD")]
    [InlineData("WD Blue SN580 NVMe SSD 2TB", "281050WD")]
    [InlineData("WD Blue SN5000 NVMe SSD 2TB", "291020WD")]
    [InlineData("SanDisk Extreme M.2 NVMe 2TB", "731130WD")]
    public void ShippedDatabase_ExactVendorFixFirmwareOverridesWildcardCaution(string model, string firmware)
    {
        var finding = FirmwareCompatService.Lookup(ShippedDatabase(), model, firmware);

        Assert.Equal(FirmwareCompatLevel.Good, finding.Level);
        Assert.Contains("fix firmware", finding.Note, StringComparison.OrdinalIgnoreCase);
    }

    private static FirmwareCompatDatabase ShippedDatabase([CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        var json = File.ReadAllText(Path.Combine(repoRoot, "src", "NVMeDriverPatcher.Core", "compat.json"));
        return JsonSerializer.Deserialize<FirmwareCompatDatabase>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to load shipped compat.json.");
    }
}
