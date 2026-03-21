using System.Windows;
using System.Windows.Controls;
using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Views;

public partial class TuningPanel : UserControl
{
    public event Action<string>? LogMessage;
    private bool _suppressEvents;

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
            SliderQueueDepth.Value = current.QueueDepth ?? 32;
            SliderReadSplit.Value = current.NvmeMaxReadSplit ?? 128;
            SliderWriteSplit.Value = current.NvmeMaxWriteSplit ?? 128;
            SliderQueueCount.Value = current.IoSubmissionQueueCount ?? 0;
            UpdateLabels();
        }
        catch { TuningStatus.Text = "Unable to read current values"; }
        finally { _suppressEvents = false; }
    }

    private void UpdateLabels()
    {
        QueueDepthValue.Text = ((int)SliderQueueDepth.Value).ToString();
        ReadSplitValue.Text = ((int)SliderReadSplit.Value).ToString();
        WriteSplitValue.Text = ((int)SliderWriteSplit.Value).ToString();
        QueueCountValue.Text = ((int)SliderQueueCount.Value).ToString();
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents) return;
        UpdateLabels();
    }

    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string profileName) return;

        var profile = profileName switch
        {
            "Performance" => TuningProfile.Performance,
            "Balanced" => TuningProfile.Balanced,
            "PowerSave" => TuningProfile.PowerSave,
            _ => TuningProfile.Balanced
        };

        _suppressEvents = true;
        SliderQueueDepth.Value = profile.QueueDepth ?? 32;
        SliderReadSplit.Value = profile.NvmeMaxReadSplit ?? 128;
        SliderWriteSplit.Value = profile.NvmeMaxWriteSplit ?? 128;
        SliderQueueCount.Value = profile.IoSubmissionQueueCount ?? 0;
        UpdateLabels();
        _suppressEvents = false;

        TuningStatus.Text = $"{profileName} profile loaded (not yet applied)";
    }

    private void ApplyTuning_Click(object sender, RoutedEventArgs e)
    {
        var profile = new TuningProfile
        {
            QueueDepth = (int)SliderQueueDepth.Value,
            NvmeMaxReadSplit = (int)SliderReadSplit.Value,
            NvmeMaxWriteSplit = (int)SliderWriteSplit.Value,
            IoSubmissionQueueCount = (int)SliderQueueCount.Value
        };

        try
        {
            TuningService.ApplyProfile(profile);
            TuningStatus.Text = "Applied. Reboot required.";
            LogMessage?.Invoke($"StorNVMe tuning applied: QD={profile.QueueDepth}, ReadSplit={profile.NvmeMaxReadSplit}, WriteSplit={profile.NvmeMaxWriteSplit}, Queues={profile.IoSubmissionQueueCount}");
        }
        catch (Exception ex)
        {
            TuningStatus.Text = $"Failed: {ex.Message}";
            LogMessage?.Invoke($"StorNVMe tuning failed: {ex.Message}");
        }
    }

    private void ResetTuning_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TuningService.ResetToDefaults();
            LoadCurrentValues();
            TuningStatus.Text = "Reset to defaults. Reboot required.";
            LogMessage?.Invoke("StorNVMe parameters reset to defaults");
        }
        catch (Exception ex)
        {
            TuningStatus.Text = $"Reset failed: {ex.Message}";
        }
    }
}
