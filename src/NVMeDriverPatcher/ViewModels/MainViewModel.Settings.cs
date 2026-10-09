using System.Windows;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.ViewModels;

// Settings & preferences partial of MainViewModel. Holds the [ObservableProperty]-generated
// OnXxxChanged hooks, the trailing-edge debounced save pipeline, and the helpers that keep
// Config in sync with the visible UI fields. Split out of the main file purely to reduce
// the cognitive load there — same class, identical behavior.
public partial class MainViewModel
{
    // Trailing-edge throttle so a rapid burst of toggles only writes once at the end.
    // DispatcherTimer lives on the UI thread; OnClosing() stops it before the final save.
    private System.Windows.Threading.DispatcherTimer? _settingsSaveDebouncer;

    // Group Policy pins (see _policyOverlay). Editable means no policy value is set for it.
    private const string PolicyPinnedToolTip = "Set by Group Policy";
    public bool PatchProfileEditable => _policyOverlay.PatchProfile is null;
    public bool ServerKeyEditable => _policyOverlay.IncludeServerKey is null;
    public bool StandaloneFutureEditable => _policyOverlay.IncludeStandaloneFuture is null && IsFullModeSelected;

    public string SafeProfileToolTip => PatchProfileEditable
        ? "Writes the primary flag plus Safe Boot entries. This is the lowest-risk default."
        : PolicyPinnedToolTip;
    public string FullProfileToolTip => PatchProfileEditable
        ? "Adds related optimization flags. Use only when the machine-specific evidence supports the higher-risk profile."
        : PolicyPinnedToolTip;
    public string StandaloneFutureToolTip => _policyOverlay.IncludeStandaloneFuture is null
        ? "Full profile only. While 156965516 (Standalone_Future) is set, DISM /ScanHealth reports component store corruption on 24H2. SFC stays clean, and it clears once the value is removed."
        : PolicyPinnedToolTip;
    public string ServerKeyToolTip => ServerKeyEditable
        ? "Also write feature flag 1176759950, the key Microsoft published for Windows Server 2025."
        : PolicyPinnedToolTip;

    public void SetThemeMode(AppThemeMode mode)
    {
        mode = ThemeService.NormalizeMode(mode);
        Config.ThemeMode = mode;
        ThemeService.ApplyMode(mode);
        RefreshThemeModeSummary();

        if (!_suppressConfigWrites)
        {
            try { ConfigService.Save(Config); } catch { }
        }
    }

    public void RefreshThemeModeSummary()
    {
        ThemeModeSummaryText = ThemeService.GetModeDescription(Config.ThemeMode);
    }

    // The *Color properties hold theme resource keys that StrToBrush turns into a brush only when its
    // binding updates. A theme switch replaces every brush, so the status chip, stage markers and
    // badges kept the previous theme's colors until their state next changed. Re-raise the keys.
    private static readonly string[] ThemeBrushKeyProperties = typeof(MainViewModel).GetProperties()
        .Where(p => p.PropertyType == typeof(string) && p.Name.EndsWith("Color", StringComparison.Ordinal))
        .Select(p => p.Name)
        .ToArray();

    internal static IReadOnlyList<string> ThemeBrushKeyPropertyNames => ThemeBrushKeyProperties;

    public void RefreshThemeBrushes()
    {
        foreach (var name in ThemeBrushKeyProperties)
            OnPropertyChanged(name);
    }

    // Persist settings shortly after a change so a crash before normal close doesn't lose
    // the user's preferences. Trailing-edge throttle — see field comment above.
    private void DebouncedSaveSettings()
    {
        if (Application.Current is null) return;
        if (_settingsSaveDebouncer is null)
        {
            _settingsSaveDebouncer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(750)
            };
            _settingsSaveDebouncer.Tick += (_, _) =>
            {
                _settingsSaveDebouncer?.Stop();
                try
                {
                    SyncConfigFromUI();
                    ConfigService.Save(Config);
                }
                catch { /* Best-effort */ }
            };
        }
        _settingsSaveDebouncer.Stop();
        _settingsSaveDebouncer.Start();
    }

    partial void OnIncludeServerKeyChanged(bool value)
    {
        UpdateChangePlan();
        RefreshOptionalFlagRows();
        DebouncedSaveSettings();
    }

    partial void OnIncludeStandaloneFutureChanged(bool value)
    {
        UpdateChangePlan();
        RefreshOptionalFlagRows();
        DebouncedSaveSettings();
    }

    partial void OnSkipWarningsChanged(bool value)
    {
        UpdateChangePlan();
        DebouncedSaveSettings();
    }

    partial void OnAutoSaveLogChanged(bool value)
    {
        UpdateActivitySummary();
        DebouncedSaveSettings();
    }

    partial void OnEnableToastsChanged(bool value) => DebouncedSaveSettings();

    partial void OnWriteEventLogChanged(bool value)
    {
        UpdateActivitySummary();
        DebouncedSaveSettings();
    }

    partial void OnRestartDelayTextChanged(string value) => DebouncedSaveSettings();

    partial void OnButtonsEnabledChanged(bool value)
    {
        RefreshMutationActionAvailability();
        UpdateRecommendedActions();
    }
    partial void OnApplyEnabledChanged(bool value) => UpdateRecommendedActions();
    partial void OnIsLoadingChanged(bool value)
    {
        UpdateRecommendedActions();
        if (!value)
            UpdateWorkflowGuide();
    }

    /// <summary>
    /// Pushes the visible settings fields back onto <see cref="Config"/>. Called from every
    /// save path (debounced tick, explicit Apply commit, OnClosing) so persistence and
    /// in-memory state never drift. Free-text fields like RestartDelay are re-snapped to the
    /// nearest valid value instead of silently clamping.
    /// </summary>
    public void SyncConfigFromUI()
    {
        // Pinned fields keep the policy value; the toggles are disabled, this covers code paths.
        if (_policyOverlay.IncludeServerKey is null) Config.IncludeServerKey = IncludeServerKey;
        if (_policyOverlay.IncludeStandaloneFuture is null) Config.IncludeStandaloneFuture = IncludeStandaloneFuture;
        if (_policyOverlay.SkipWarnings is null) Config.SkipWarnings = SkipWarnings;
        Config.AutoSaveLog = AutoSaveLog;
        Config.EnableToasts = EnableToasts;
        Config.WriteEventLog = WriteEventLog;
        // AppConfig.RestartDelay setter clamps to 0..3600. We additionally enforce the UI's
        // documented 5..300s range here so an invalid free-text entry never silently becomes
        // a 3600-second restart countdown.
        if (int.TryParse(RestartDelayText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int delay)
            && delay >= 5 && delay <= 300)
        {
            Config.RestartDelay = delay;
        }
        else
        {
            // Reset visible field to the last valid value so the user sees what was kept.
            RestartDelayText = Config.RestartDelay.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
