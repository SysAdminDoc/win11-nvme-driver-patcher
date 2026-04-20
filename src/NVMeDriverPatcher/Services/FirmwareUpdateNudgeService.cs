namespace NVMeDriverPatcher.Services;

public class FirmwareUpdateNudge
{
    public string DriveModel { get; set; } = string.Empty;
    public string CurrentFirmware { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public string UpdateToolUrl { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

// Best-effort "we see you're on firmware X — here's where to grab the latest" hint. Works
// off a shipped map of `{vendor substring → vendor update tool URL}` — we never try to
// hotlink a specific firmware .zip because those URLs rot constantly. Vendors get a stable
// "tool landing page" URL instead.
public static class FirmwareUpdateNudgeService
{
    // Stable landing pages from each vendor. Kept conservative — a broken link here lives
    // in the app for a long time. If a vendor redomains, update here and ship a patch.
    private static readonly (string VendorPattern, string Vendor, string Url)[] VendorTools =
    {
        ("samsung",          "Samsung",         "https://semiconductor.samsung.com/consumer-storage/support/tools/"),
        ("wd_black",         "Western Digital", "https://dashboard.wd.com/"),
        ("wdc ",             "Western Digital", "https://dashboard.wd.com/"),
        ("western digital",  "Western Digital", "https://dashboard.wd.com/"),
        ("crucial",          "Crucial",         "https://www.crucial.com/support/storage-executive"),
        ("ct",               "Crucial",         "https://www.crucial.com/support/storage-executive"),
        ("sk hynix",         "SK hynix",        "https://ssd.skhynix.com/downloads/"),
        ("hynix",            "SK hynix",        "https://ssd.skhynix.com/downloads/"),
        ("kingston",         "Kingston",        "https://www.kingston.com/unitedstates/us/support/technical/ssdmanager"),
        ("sabrent",          "Sabrent",         "https://www.sabrent.com/downloads/"),
        ("intel",            "Intel / Solidigm","https://www.solidigm.com/support-page/support-tools-downloads.html"),
        ("solidigm",         "Intel / Solidigm","https://www.solidigm.com/support-page/support-tools-downloads.html"),
        ("seagate",          "Seagate",         "https://www.seagate.com/support/downloads/seatools/"),
        ("corsair",          "Corsair",         "https://www.corsair.com/us/en/s/downloads"),
        ("adata",            "ADATA",           "https://www.adata.com/en/support/downloads/"),
        ("xpg",              "ADATA",           "https://www.adata.com/en/support/downloads/"),
        ("phison",           "Phison / OEM",    "https://www.phison.com/en-US/"),
        ("micron",           "Micron / Crucial","https://www.crucial.com/support/storage-executive")
    };

    public static FirmwareUpdateNudge Lookup(string driveModel, string currentFirmware)
    {
        var nudge = new FirmwareUpdateNudge
        {
            DriveModel = driveModel ?? string.Empty,
            CurrentFirmware = currentFirmware ?? string.Empty
        };
        foreach (var (pattern, vendor, url) in VendorTools)
        {
            if (!string.IsNullOrEmpty(driveModel) &&
                driveModel.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                nudge.Vendor = vendor;
                nudge.UpdateToolUrl = url;
                nudge.Summary = $"{vendor} firmware check: {url}";
                return nudge;
            }
        }
        nudge.Vendor = "Unknown";
        nudge.Summary = "No vendor update page mapped for this drive model.";
        return nudge;
    }
}
