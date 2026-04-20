using System.Text.Json.Serialization;

namespace NVMeDriverPatcher.Models;

// Which set of feature flags to write. Safe is the community-recommended default as of
// 2026 because BSOD reports cluster on the two "extended" flags, while the primary flag
// alone is sufficient to swap stornvme.sys for nvmedisk.sys.
// See ROADMAP.md §0.4 and the v4.2.0 CHANGELOG for full context.
public enum PatchProfile
{
    Safe = 0,
    Full = 1
}

public class AppConfig
{
    public const string AppName = "NVMe Driver Patcher";

    // Derived from the executing assembly's InformationalVersion / AssemblyVersion so a tagged
    // release build (which sets these via dotnet publish -p:Version=...) reports the right
    // version everywhere — instead of having a hard-coded literal here drift out of sync with
    // the csproj. Falls back to the literal if the lookup ever fails.
    public static string AppVersion { get; } = ResolveAssemblyVersion();

    private static string ResolveAssemblyVersion()
    {
        try
        {
            var asm = typeof(AppConfig).Assembly;
            var infoAttr = asm
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault();
            var v = infoAttr?.InformationalVersion;
            if (!string.IsNullOrEmpty(v))
            {
                // InformationalVersion may include a "+commitsha" suffix — trim it.
                int plus = v.IndexOf('+');
                if (plus > 0) v = v.Substring(0, plus);
                return v;
            }
            var asmVer = asm.GetName().Version;
            if (asmVer is not null) return $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}";
        }
        catch { /* fall through to literal */ }
        return "4.6.0";
    }
    public const string RegistryPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides";
    public const string RegistrySubKey = @"SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides";
    public const string SafeBootMinimalPath = @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}";
    public const string SafeBootNetworkPath = @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network\{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}";
    public const string SafeBootGuid = "{75416E63-5912-4DFA-AE8F-3EFACCAFFB14}";
    public const string SafeBootValue = "Storage Disks";
    public const int MinWinBuild = 22000;
    public const int RecommendedBuild = 26100;
    public const string ServerFeatureID = "1176759950";

    // The lone flag needed to swap stornvme.sys -> nvmedisk.sys on supported builds.
    // In Safe Mode we write ONLY this (plus SafeBoot) — the two flags below are opt-in.
    public const string PrimaryFeatureID = "735209102";

    // Extended flags — gated behind Full Mode because community BSOD reports (Jan–Mar 2026
    // Overclock.net / windowsforum.com threads) correlate crashes with these two.
    public static IReadOnlyList<string> ExtendedFeatureIDs { get; } = ["1853569164", "156965516"];

    // Derived from FeatureIDs.Count so adding/removing a core flag doesn't silently leave
    // TotalComponents out of sync with what PatchService.Install actually writes.
    // (Core feature flags + 2 SafeBoot keys [Minimal, Network])
    public static int TotalComponents => FeatureIDs.Count + 2;

    // The set of feature flags actually written for a given profile, NOT counting SafeBoot
    // keys (callers add +2 for those). Safe = primary only; Full = primary + extended.
    public static IReadOnlyList<string> GetFeatureIDsForProfile(PatchProfile profile) =>
        profile == PatchProfile.Safe
            ? new[] { PrimaryFeatureID }
            : FeatureIDs;

    // Component count the installer is aiming for, given profile + optional Server 2025 key.
    public static int GetTotalComponents(PatchProfile profile, bool includeServerKey) =>
        GetFeatureIDsForProfile(profile).Count + (includeServerKey ? 1 : 0) + 2;
    public const string EventLogSourceName = "NVMe Driver Patcher";
    public const string GitHubURL = "https://github.com/SysAdminDoc/win11-nvme-driver-patcher";
    public const string DocumentationURL = "https://techcommunity.microsoft.com/blog/windowsservernewsandbestpractices/announcing-native-nvme-in-windows-server-2025-ushering-in-a-new-era-of-storage-p/4477353";
    public const string GitHubApiReleasesUrl = "https://api.github.com/repos/SysAdminDoc/win11-nvme-driver-patcher/releases/latest";

    public static IReadOnlyList<string> FeatureIDs { get; } = ["735209102", "1853569164", "156965516"];

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
    private int _restartDelay = 30;
    public int RestartDelay
    {
        get => _restartDelay;
        set => _restartDelay = Math.Clamp(value, 0, 3600);
    }
    public bool IncludeServerKey { get; set; }
    public bool SkipWarnings { get; set; }
    // Default to Safe — primary flag only. Users can opt into Full after reading the tradeoff.
    public PatchProfile PatchProfile { get; set; } = PatchProfile.Safe;
    // Schema version for future migrations. Leave loose — unknown values fall back to Safe.
    public int ConfigVersion { get; set; } = 2;
    public string? LastRun { get; set; }
    public string? LastRecoveryKitPath { get; set; }
    public string? LastDiagnosticsPath { get; set; }
    public string? LastSupportBundlePath { get; set; }
    public string? LastVerificationScriptPath { get; set; }

    // Set on successful patch apply; cleared after a post-reboot verification confirms
    // nvmedisk.sys actually bound (or flagged the user that the override was blocked).
    // ISO-8601 UTC timestamp so the next launch can compare vs OS boot time.
    public string? PendingVerificationSince { get; set; }
    public string? PendingVerificationProfile { get; set; }
    public string? LastVerifiedProfile { get; set; }
    public string? LastVerificationResult { get; set; }

    [JsonIgnore]
    public string WorkingDir { get; set; } = string.Empty;

    [JsonIgnore]
    public string ConfigFile { get; set; } = string.Empty;

    /// <summary>
    /// Records which fallback path was used so startup can log a warning when running
    /// from something other than LocalAppData. Null when LocalAppData succeeded normally.
    /// </summary>
    public static string? WorkingDirFallbackReason { get; private set; }

    public static string GetWorkingDir()
    {
        // Portable mode (v4.5) trumps everything — if the user opted in via portable.flag
        // we write state to Data\ beside the exe instead of %LocalAppData%. Catch-all on the
        // portable path so a flaky exe-dir permission doesn't wedge startup.
        try
        {
            if (Services.PortableModeService.IsPortable())
            {
                var portable = Services.PortableModeService.PortableDataPath();
                if (!string.IsNullOrEmpty(portable))
                {
                    WorkingDirFallbackReason = null;
                    return portable;
                }
            }
        }
        catch { /* fall through to per-user path */ }

        // Try in order: LocalAppData (preferred), TEMP fallback, current directory last-resort.
        // Each step is independently try-wrapped so a denied LocalAppData (rare, hardened SKUs)
        // still gets us a usable working folder instead of NRE'ing the rest of the app.
        string? localAppDataError = null;
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                var dir = Path.Combine(localAppData, "NVMePatcher");
                if (Directory.Exists(dir))
                {
                    WorkingDirFallbackReason = null;
                    return dir;
                }
                Directory.CreateDirectory(dir);
                WorkingDirFallbackReason = null;
                return dir;
            }
            localAppDataError = "LocalAppData path was empty";
        }
        catch (Exception ex)
        {
            localAppDataError = $"LocalAppData unavailable: {ex.GetType().Name}";
        }

        try
        {
            var temp = Path.Combine(Path.GetTempPath(), "NVMePatcher_Backups");
            if (!Directory.Exists(temp)) Directory.CreateDirectory(temp);
            WorkingDirFallbackReason = $"Using TEMP fallback ({localAppDataError ?? "unknown reason"})";
            return temp;
        }
        catch (Exception ex)
        {
            // Absolute last resort — current directory. Always exists. This is the most
            // concerning fallback because launching from a privileged directory (e.g.
            // C:\Windows\System32) would land DB/backups there too.
            WorkingDirFallbackReason = $"Using CurrentDirectory last-resort fallback: {Environment.CurrentDirectory} (TEMP failed: {ex.GetType().Name}; {localAppDataError ?? "unknown"})";
            return Environment.CurrentDirectory;
        }
    }
}
