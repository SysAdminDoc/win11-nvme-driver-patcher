using System.Text.Json.Serialization;

namespace NVMeDriverPatcher.Models;

public class AppConfig
{
    public const string AppName = "NVMe Driver Patcher";
    public const string AppVersion = "4.0.0";
    public const string RegistryPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides";
    public const string RegistrySubKey = @"SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides";
    public const string SafeBootMinimalPath = @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}";
    public const string SafeBootNetworkPath = @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}";
    public const string SafeBootGuid = "{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}";
    public const string SafeBootValue = "Storage Disks";
    public const int MinWinBuild = 22000;
    public const int RecommendedBuild = 26100;
    public const int TotalComponents = 5;
    public const string ServerFeatureID = "1176759950";
    public const string EventLogSourceName = "NVMe Driver Patcher";
    public const string GitHubURL = "https://github.com/SysAdminDoc/win11-nvme-driver-patcher";
    public const string DocumentationURL = "https://techcommunity.microsoft.com/blog/windowsservernewsandbestpractices/announcing-native-nvme-in-windows-server-2025-ushering-in-a-new-era-of-storage-p/4477353";
    public const string GitHubApiReleasesUrl = "https://api.github.com/repos/SysAdminDoc/win11-nvme-driver-patcher/releases/latest";

    public static readonly string[] FeatureIDs = ["735209102", "1853569164", "156965516"];

    public static readonly Dictionary<string, string> FeatureNames = new()
    {
        ["735209102"] = "NativeNVMeStackForGeClient (Primary enable)",
        ["1853569164"] = "UxAccOptimization (Extended functionality)",
        ["156965516"] = "Standalone_Future (Performance optimizations)",
        ["1176759950"] = "Microsoft Official (Server 2025 key)"
    };

    // Persisted settings
    public bool AutoSaveLog { get; set; } = true;
    public bool EnableToasts { get; set; } = true;
    public bool WriteEventLog { get; set; } = true;
    public int RestartDelay { get; set; } = 30;
    public bool IncludeServerKey { get; set; }
    public bool SkipWarnings { get; set; }
    public string? LastRun { get; set; }
    public string? LastRecoveryKitPath { get; set; }
    public string? LastDiagnosticsPath { get; set; }
    public string? LastVerificationScriptPath { get; set; }

    [JsonIgnore]
    public string WorkingDir { get; set; } = string.Empty;

    [JsonIgnore]
    public string ConfigFile { get; set; } = string.Empty;

    public static string GetWorkingDir()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(localAppData, "NVMePatcher");
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); }
            catch
            {
                dir = Path.Combine(Path.GetTempPath(), "NVMePatcher_Backups");
                Directory.CreateDirectory(dir);
            }
        }
        return dir;
    }
}
