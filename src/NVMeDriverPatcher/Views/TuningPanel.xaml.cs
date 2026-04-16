using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Views;

public partial class TuningPanel : UserControl
{
    public event Action<string>? LogMessage;

    private bool _suppressEvents;
    private TuningProfile _loadedProfile = TuningProfile.Balanced;
    private bool _loadedProfileHasExplicitOverrides;
    private static readonly BrushConverter BrushConverter = new();
    private static readonly TuningProfile[] Presets = TuningProfile.GetPresets();

    public TuningPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadCurrentValues();
    }

    private void LoadCurrentValues()
    {
        _suppressEvents = true;
        try
        {
            var current = TuningService.GetCurrentParameters();
            _loadedProfileHasExplicitOverrides = HasAnyStoredOverride(current);
            _loadedProfile = NormalizeProfile(current);
            ApplyProfileToSliders(_loadedProfile);
            UpdateLabels();
            UpdateProfileSummary();
            UpdateChangeSummary();
            SetStatus(
                _loadedProfileHasExplicitOverrides
                    ? "Loaded current StorNVMe registry overrides. Apply only if you want to replace them."
                    : "Loaded Windows defaults. Apply only if you want to create explicit StorNVMe overrides.",
                "muted");
        }
        catch
        {
            SetStatus("Unable to read current values.", "danger");
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void ApplyProfileToSliders(TuningProfile profile)
    {
        SliderQueueDepth.Value = profile.QueueDepth ?? 32;
        SliderReadSplit.Value = profile.NvmeMaxReadSplit ?? 128;
        SliderWriteSplit.Value = profile.NvmeMaxWriteSplit ?? 128;
        SliderQueueCount.Value = profile.IoSubmissionQueueCount ?? 0;
        SliderIdlePowerTimeout.Value = profile.IdlePowerTimeout ?? 100;
        SliderStandbyPowerTimeout.Value = profile.StandbyPowerTimeout ?? 0;
    }

    private TuningProfile CreateProfileFromSliders()
    {
        return new TuningProfile
        {
            Name = "Custom",
            QueueDepth = (int)SliderQueueDepth.Value,
            NvmeMaxReadSplit = (int)SliderReadSplit.Value,
            NvmeMaxWriteSplit = (int)SliderWriteSplit.Value,
            IoSubmissionQueueCount = (int)SliderQueueCount.Value,
            IdlePowerTimeout = (int)SliderIdlePowerTimeout.Value,
            StandbyPowerTimeout = (int)SliderStandbyPowerTimeout.Value
        };
    }

    private void UpdateLabels()
    {
        QueueDepthValue.Text = ((int)SliderQueueDepth.Value).ToString();
        ReadSplitValue.Text = ((int)SliderReadSplit.Value).ToString();
        WriteSplitValue.Text = ((int)SliderWriteSplit.Value).ToString();

        int queueCount = (int)SliderQueueCount.Value;
        QueueCountValue.Text = queueCount == 0 ? "Auto" : queueCount.ToString();
        IdlePowerValue.Text = FormatTimeout((int)SliderIdlePowerTimeout.Value);
        StandbyPowerValue.Text = FormatTimeout((int)SliderStandbyPowerTimeout.Value);
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents)
            return;

        UpdateLabels();
        UpdateProfileSummary();
        UpdateChangeSummary();
        SetStatus(BuildPendingStatusMessage(), "info");
    }

    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string profileName)
            return;

        var profile = profileName switch
        {
            "Performance" => TuningProfile.Performance,
            "Balanced" => TuningProfile.Balanced,
            "PowerSave" => TuningProfile.PowerSave,
            _ => TuningProfile.Balanced
        };

        _suppressEvents = true;
        ApplyProfileToSliders(profile);
        UpdateLabels();
        _suppressEvents = false;

        UpdateProfileSummary();
        UpdateChangeSummary();
        SetStatus(BuildPendingStatusMessage(), "info");
    }

    private void ApplyTuning_Click(object sender, RoutedEventArgs e)
    {
        var profile = CreateProfileFromSliders();
        var matchedPreset = FindMatchingPreset(profile);
        if (matchedPreset is not null)
        {
            profile.Name = matchedPreset.Name;
            profile.Description = matchedPreset.Description;
        }

        try
        {
            bool success = TuningService.ApplyProfile(profile, msg => LogMessage?.Invoke(msg));
            if (success)
            {
                _loadedProfile = NormalizeProfile(profile);
                _loadedProfileHasExplicitOverrides = true;
                UpdateProfileSummary();
                UpdateChangeSummary();
                string queueLabel = profile.IoSubmissionQueueCount == 0 ? "Auto" : profile.IoSubmissionQueueCount?.ToString() ?? "Auto";
                SetStatus($"{profile.Name} tuning written to the registry. Reboot required before it becomes active.", "success");
                LogMessage?.Invoke(
                    $"StorNVMe tuning applied: QD={profile.QueueDepth}, ReadSplit={profile.NvmeMaxReadSplit}, WriteSplit={profile.NvmeMaxWriteSplit}, Queues={queueLabel}, IdleTimeout={profile.IdlePowerTimeout}ms, StandbyTimeout={profile.StandbyPowerTimeout}ms");
            }
            else
            {
                SetStatus("Apply finished with verification issues. Review the activity log before rebooting.", "warning");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Failed: {ex.Message}", "danger");
            LogMessage?.Invoke($"StorNVMe tuning failed: {ex.Message}");
        }
    }

    private void ResetTuning_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool success = TuningService.ResetToDefaults(msg => LogMessage?.Invoke(msg));
            LoadCurrentValues();
            SetStatus(
                success
                    ? "All StorNVMe overrides were removed. Windows defaults return after reboot."
                    : "Reset finished with some errors. Review the activity log before rebooting.",
                success ? "success" : "warning");
            LogMessage?.Invoke("StorNVMe parameters reset to defaults");
        }
        catch (Exception ex)
        {
            SetStatus($"Reset failed: {ex.Message}", "danger");
        }
    }

    private void UpdateProfileSummary()
    {
        var profile = CreateProfileFromSliders();
        var matchedPreset = FindMatchingPreset(profile);

        if (matchedPreset is not null)
        {
            ProfileTitleText.Text = $"{matchedPreset.Name} profile ready";
            ProfileDescriptionText.Text = matchedPreset.Description;
            ProfileImpactText.Text = matchedPreset.Name switch
            {
                "Performance" => "Best for well-cooled desktops and benchmark-driven tuning. Expect more heat and less aggressive power saving.",
                "Balanced" => "Closest to Windows defaults. This is the safest starting point when you want a clear baseline with mild tuning intent.",
                _ => "Better suited to laptops or battery-sensitive systems. Throughput may dip, but idle behavior is calmer and more power-aware."
            };
        }
        else
        {
            ProfileTitleText.Text = "Custom profile";
            ProfileDescriptionText.Text = "These values no longer match a built-in preset. Change one dimension at a time so you can tell what helped and what did not.";
            ProfileImpactText.Text = "Queue count 0 keeps automatic queue allocation, while timeout values of 0 disable that power-saving path. Treat custom values like an experiment until they earn a place.";
        }

        SetActiveProfileButton(matchedPreset?.Name);
    }

    private void UpdateChangeSummary()
    {
        var pendingProfile = CreateProfileFromSliders();
        int changedSettingCount = CountDifferences(_loadedProfile, pendingProfile);
        var baselinePreset = FindMatchingPreset(_loadedProfile);
        var pendingPreset = FindMatchingPreset(pendingProfile);

        CurrentConfigText.Text = _loadedProfileHasExplicitOverrides
            ? $"{(baselinePreset?.Name ?? "Custom")} values are currently stored in the registry. {BuildCompactSummary(_loadedProfile)}"
            : $"Windows defaults are effectively active. {BuildCompactSummary(_loadedProfile)}";

        PendingConfigText.Text = changedSettingCount == 0
            ? "No pending changes. Apply stays disabled until you move a control or choose a different preset."
            : $"{changedSettingCount} setting {Pluralize(changedSettingCount, "change")} pending. {(pendingPreset?.Name ?? "Custom")} will write {BuildCompactSummary(pendingProfile)}";

        BtnApplyTuning.IsEnabled = changedSettingCount > 0;
    }

    private static TuningProfile? FindMatchingPreset(TuningProfile profile)
    {
        return Presets.FirstOrDefault(p =>
            p.QueueDepth == profile.QueueDepth &&
            p.NvmeMaxReadSplit == profile.NvmeMaxReadSplit &&
            p.NvmeMaxWriteSplit == profile.NvmeMaxWriteSplit &&
            p.IoSubmissionQueueCount == profile.IoSubmissionQueueCount &&
            p.IdlePowerTimeout == profile.IdlePowerTimeout &&
            p.StandbyPowerTimeout == profile.StandbyPowerTimeout);
    }

    private void SetActiveProfileButton(string? activeProfileName)
    {
        SetButtonState(BtnPerformance, activeProfileName == TuningProfile.Performance.Name);
        SetButtonState(BtnBalanced, activeProfileName == TuningProfile.Balanced.Name);
        SetButtonState(BtnPowerSave, activeProfileName == TuningProfile.PowerSave.Name);
    }

    private void SetButtonState(System.Windows.Controls.Button button, bool isActive)
    {
        button.Background = ResolveBrush(isActive ? "AccentBg" : "BgMedium", isActive ? "#FF0C2D5E" : "#FF131318");
        button.BorderBrush = ResolveBrush(isActive ? "Accent" : "Border", isActive ? "#FF60A5FA" : "#FF25252C");
        button.Foreground = ResolveBrush(isActive ? "Accent" : "TextSecondary", isActive ? "#FF60A5FA" : "#FFD4D4D8");
    }

    private void SetStatus(string message, string tone)
    {
        TuningStatus.Text = message;

        TuningStatus.Foreground = tone switch
        {
            "success" => ResolveBrush("Green", "#FF22C55E"),
            "warning" => ResolveBrush("Yellow", "#FFF59E0B"),
            "danger" => ResolveBrush("Red", "#FFEF4444"),
            "info" => ResolveBrush("Accent", "#FF60A5FA"),
            _ => ResolveBrush("TextDim", "#FFA1B0C8")
        };

        TuningStatusCard.Background = tone switch
        {
            "success" => ResolveBrush("GreenBg", "#120B2919"),
            "warning" => ResolveBrush("YellowBg", "#14F59E0B"),
            "danger" => ResolveBrush("RedBg", "#14EF4444"),
            "info" => ResolveBrush("AccentBg", "#180C2D5E"),
            _ => ResolveBrush("SurfaceInset", "#FF0F1014")
        };

        TuningStatusCard.BorderBrush = tone switch
        {
            "success" => ResolveBrush("Green", "#FF22C55E"),
            "warning" => ResolveBrush("Yellow", "#FFF59E0B"),
            "danger" => ResolveBrush("Red", "#FFEF4444"),
            "info" => ResolveBrush("Accent", "#FF60A5FA"),
            _ => ResolveBrush("Border", "#FF25252C")
        };
    }

    private Brush ResolveBrush(string resourceKey, string fallbackHex)
    {
        return TryFindResource(resourceKey) as Brush
            ?? (Brush)BrushConverter.ConvertFromString(fallbackHex)!;
    }

    private string BuildPendingStatusMessage()
    {
        int changedSettingCount = CountDifferences(_loadedProfile, CreateProfileFromSliders());
        return changedSettingCount == 0
            ? "No pending tuning changes. The sliders match the current baseline."
            : $"{changedSettingCount} tuning {Pluralize(changedSettingCount, "change")} pending. Apply to write these values to the registry.";
    }

    private static TuningProfile NormalizeProfile(TuningProfile profile)
    {
        return new TuningProfile
        {
            Name = profile.Name,
            Description = profile.Description,
            QueueDepth = profile.QueueDepth ?? TuningProfile.Balanced.QueueDepth,
            NvmeMaxReadSplit = profile.NvmeMaxReadSplit ?? TuningProfile.Balanced.NvmeMaxReadSplit,
            NvmeMaxWriteSplit = profile.NvmeMaxWriteSplit ?? TuningProfile.Balanced.NvmeMaxWriteSplit,
            IoSubmissionQueueCount = profile.IoSubmissionQueueCount ?? TuningProfile.Balanced.IoSubmissionQueueCount,
            IdlePowerTimeout = profile.IdlePowerTimeout ?? TuningProfile.Balanced.IdlePowerTimeout,
            StandbyPowerTimeout = profile.StandbyPowerTimeout ?? TuningProfile.Balanced.StandbyPowerTimeout
        };
    }

    private static bool HasAnyStoredOverride(TuningProfile profile)
    {
        return profile.QueueDepth is not null
            || profile.NvmeMaxReadSplit is not null
            || profile.NvmeMaxWriteSplit is not null
            || profile.IoSubmissionQueueCount is not null
            || profile.IdlePowerTimeout is not null
            || profile.StandbyPowerTimeout is not null;
    }

    private static int CountDifferences(TuningProfile baseline, TuningProfile candidate)
    {
        int differences = 0;

        if (baseline.QueueDepth != candidate.QueueDepth) differences++;
        if (baseline.NvmeMaxReadSplit != candidate.NvmeMaxReadSplit) differences++;
        if (baseline.NvmeMaxWriteSplit != candidate.NvmeMaxWriteSplit) differences++;
        if (baseline.IoSubmissionQueueCount != candidate.IoSubmissionQueueCount) differences++;
        if (baseline.IdlePowerTimeout != candidate.IdlePowerTimeout) differences++;
        if (baseline.StandbyPowerTimeout != candidate.StandbyPowerTimeout) differences++;

        return differences;
    }

    private static string BuildCompactSummary(TuningProfile profile)
    {
        return $"QD {profile.QueueDepth}, Read {profile.NvmeMaxReadSplit}, Write {profile.NvmeMaxWriteSplit}, Queues {FormatQueueCount(profile.IoSubmissionQueueCount)}, Idle {FormatTimeout(profile.IdlePowerTimeout ?? 0)}, Standby {FormatTimeout(profile.StandbyPowerTimeout ?? 0)}.";
    }

    private static string FormatQueueCount(int? value)
    {
        return value is null or 0 ? "Auto" : value.Value.ToString();
    }

    private static string Pluralize(int count, string singular, string? plural = null)
    {
        return count == 1 ? singular : plural ?? $"{singular}s";
    }

    private static string FormatTimeout(int value)
    {
        return value == 0 ? "Disabled" : $"{value} ms";
    }
}
