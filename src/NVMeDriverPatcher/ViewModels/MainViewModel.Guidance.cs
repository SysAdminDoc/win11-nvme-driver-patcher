using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.ViewModels;

// Guidance / workflow partial of MainViewModel. Responsible for the right-rail narrative —
// the apply/remove button tooltips, the three-stage workflow guide, and the
// recommended-next-step primary/secondary buttons. Each method is pure state-projection:
// reads from `_preflight`, `Config`, and the patch-status dictionary, then writes into the
// [ObservableProperty]-generated fields declared in the main file. Split out so the
// end-user copy (which dominates this cluster) stays easy to audit without scrolling past
// 1,000 lines of unrelated command handlers.
public partial class MainViewModel
{
    private void UpdateActionGuidance(PatchStatus? knownStatus = null)
    {
        if (_preflight is null)
        {
            ApplyButtonTooltipText = "Readiness checks are still running.";
            RemoveButtonTooltipText = RemoveUnavailableText;
            return;
        }

        if (!_mutationAllowedByRecovery)
        {
            ApplyButtonTooltipText = MutationBlockedReason;
            var recoveryStatus = knownStatus ?? RegistryService.GetPatchStatus();
            RemoveButtonTooltipText = recoveryStatus.Applied || recoveryStatus.Partial
                ? "Remove remains available to recover the staged or partial registry state."
                : RemoveUnavailableText;
            return;
        }

        // The build-rule policy disables Apply too, and this method used to fall straight through
        // to the "Apply is ready" text below. ActionButton sets ToolTipService.ShowOnDisabled, so
        // the disabled button actively advertised that it was ready to run. The whole bundled
        // ruleset can also go stale at once, which makes this a common state rather than an exotic
        // one, so it needs the same honest treatment as the recovery block above.
        if (!_mutationAllowedByBuild)
        {
            ApplyButtonTooltipText = string.IsNullOrWhiteSpace(MutationBlockedReason)
                ? "Apply is disabled because this Windows build is not covered by a current, trusted build rule."
                : MutationBlockedReason;
            var buildStatus = knownStatus ?? RegistryService.GetPatchStatus();
            RemoveButtonTooltipText = buildStatus.Applied || buildStatus.Partial
                ? "Remove remains available to reverse a patch that is already applied."
                : RemoveUnavailableText;
            return;
        }

        var status = knownStatus ?? RegistryService.GetPatchStatus();
        int plannedComponentCount = GetPlannedComponentCount();

        if (_criticalCount > 0)
        {
            string blockerSummary = BuildBlockingActionSummary();
            ApplyButtonTooltipText = $"Apply is blocked until the critical checks are resolved. {blockerSummary}";
            RemoveButtonTooltipText = status.Applied || status.Partial
                ? "Remove can still revert the staged or partial registry state."
                : RemoveUnavailableText;
            return;
        }

        if (_preflight.NativeNVMeStatus?.IsActive == true && !status.Applied && !status.Partial)
        {
            ApplyButtonTooltipText = $"Apply will stage {plannedComponentCount} patch components plus recovery helpers, even though the native driver path is already live.";
            RemoveButtonTooltipText = RemoveUnavailableText;
            return;
        }

        if (status.Applied)
        {
            ApplyButtonTooltipText = $"Reinstall will refresh {plannedComponentCount} patch components, Safe Mode protections, and local snapshots.";
            RemoveButtonTooltipText = "Remove clears the staged patch keys and Safe Mode protections, then requires a reboot to restore the legacy path.";
            return;
        }

        if (status.Partial)
        {
            ApplyButtonTooltipText = $"Repair will attempt to complete the intended {plannedComponentCount} patch components and refresh recovery helpers.";
            RemoveButtonTooltipText = "Remove can clean up the partial registry state before you retry the migration.";
            return;
        }

        if (_warningCount > 0)
        {
            ApplyButtonTooltipText = $"Apply will stage {plannedComponentCount} patch components, Safe Mode protections, and local recovery material once you accept the advisory tradeoffs.";
            RemoveButtonTooltipText = RemoveUnavailableText;
            return;
        }

        ApplyButtonTooltipText = $"Apply is ready. It will stage {plannedComponentCount} patch components, add Safe Mode protections, save snapshots, and require a reboot before the live driver path changes.";
        RemoveButtonTooltipText = RemoveUnavailableText;
    }

    private string BuildBlockingActionSummary()
    {
        if (_preflight is null)
            return "The current machine state is still unknown.";

        var blockingChecks = _preflight.Checks
            .Where(pair => pair.Value.Critical && pair.Value.Status == CheckStatus.Fail)
            .Select(pair => $"{GetCheckDisplayName(pair.Key)}: {pair.Value.Message}")
            .Take(2)
            .ToList();

        if (blockingChecks.Count == 0)
            return "Resolve the blocking readiness items and run the scan again.";

        return string.Join(" ", blockingChecks);
    }

    private static string GetCheckDisplayName(string key)
    {
        return key switch
        {
            "WindowsVersion" => "Windows build",
            "NVMeDrives" => "NVMe inventory",
            "BitLocker" => "BitLocker",
            "VeraCrypt" => "VeraCrypt",
            "LaptopPower" => "Power model",
            "DriverStatus" => "Driver state",
            "ThirdPartyDriver" => "Third-party driver",
            "Compatibility" => "Compatibility",
            "SystemProtection" => "System protection",
            "BypassIO" => "BypassIO",
            "StorPortOverrides" => "StorPort override",
            _ => key
        };
    }

    private void UpdateWorkflowGuide(PatchStatus? knownStatus = null)
    {
        var patchStatus = knownStatus ?? RegistryService.GetPatchStatus();
        bool nativeDriverActive = _preflight?.NativeNVMeStatus?.IsActive == true;
        bool prepEvidenceReady = _hasBackupFiles || _hasBenchmarkHistory || HasRecoveryKit;
        bool prepReady = _criticalCount == 0 && _hasBackupFiles && (_hasBenchmarkHistory || HasRecoveryKit);
        bool validationEvidenceReady = _hasBenchmarkHistory || HasDiagnosticsReport;

        if (IsLoading)
        {
            ScanStageStateText = "Readiness scan in progress";
            ScanStageDetailText = "Checking Windows build support, the drive inventory and the hard safety blockers.";
            ScanStageColor = "Accent";
        }
        else if (_criticalCount > 0)
        {
            ScanStageStateText = $"{_criticalCount} blocking {Pluralize(_criticalCount, "issue")}";
            ScanStageDetailText = "Patch actions stay locked until every critical check passes on a clean scan.";
            ScanStageColor = "Red";
        }
        else if (_warningCount > 0)
        {
            ScanStageStateText = $"Clear with {_warningCount} {Pluralize(_warningCount, "warning")}";
            ScanStageDetailText = "The hard safety checks passed. Read the warnings before you apply.";
            ScanStageColor = "Yellow";
        }
        else
        {
            ScanStageStateText = "Readiness scan clear";
            ScanStageDetailText = "This build and its drives passed every critical check.";
            ScanStageColor = "Green";
        }

        if (_criticalCount > 0)
        {
            PreparationStageStateText = "Blocked by readiness checks";
            PreparationStageDetailText = "Fix the critical readiness issues first. Backups come after that.";
            PreparationStageColor = "Red";
        }
        else if (prepReady)
        {
            PreparationStageStateText = "Prepared for a controlled change";
            PreparationStageDetailText = "Backups and a baseline are in place, so rolling back is straightforward.";
            PreparationStageColor = "Green";
        }
        else if (prepEvidenceReady)
        {
            PreparationStageStateText = "Partially prepared";
            PreparationStageDetailText = "Some safety files are in place. Add the missing backup or baseline before you apply.";
            PreparationStageColor = "Yellow";
        }
        else
        {
            PreparationStageStateText = "Capture baseline and safety artifacts";
            PreparationStageDetailText = "Start with a registry backup and a recovery kit. A benchmark before the patch is optional, but it gives you something to compare against.";
            PreparationStageColor = "Accent";
        }

        if (nativeDriverActive)
        {
            RestartStageStateText = "Migration is live";
            RestartStageDetailText = "Windows is already running on nvmedisk.sys. There's nothing left to restart for.";
            RestartStageColor = "Green";
        }
        else if (patchStatus.Applied)
        {
            RestartStageStateText = "Restart required";
            RestartStageDetailText = "The patch is staged. Windows stays on the old driver until you restart.";
            RestartStageColor = "Yellow";
        }
        else if (patchStatus.Partial)
        {
            RestartStageStateText = "Staging is incomplete";
            RestartStageDetailText = "Only some patch components are present. Repair or remove the partial patch before you restart.";
            RestartStageColor = "Red";
        }
        else
        {
            RestartStageStateText = "Patch not staged yet";
            RestartStageDetailText = "After you apply the patch, this step tells you when to restart.";
            RestartStageColor = "TextDim";
        }

        if (nativeDriverActive && validationEvidenceReady && HasVerificationScript)
        {
            ValidationStageStateText = "Validated with local evidence";
            ValidationStageDetailText = "The native driver is active, and this machine has local evidence to back it up.";
            ValidationStageColor = "Green";
        }
        else if (nativeDriverActive && (validationEvidenceReady || HasVerificationScript))
        {
            ValidationStageStateText = "Validation is in progress";
            ValidationStageDetailText = "The native driver is live. Add a benchmark comparison or a diagnostics export to confirm it.";
            ValidationStageColor = "Yellow";
        }
        else if (patchStatus.Applied)
        {
            ValidationStageStateText = "Available after reboot";
            ValidationStageDetailText = "After the restart, run a benchmark or export diagnostics to confirm the change.";
            ValidationStageColor = "Accent";
        }
        else
        {
            ValidationStageStateText = "Starts after the restart";
            ValidationStageDetailText = "Once Windows restarts on the native driver, benchmarks and diagnostics confirm the change on this machine.";
            ValidationStageColor = "TextDim";
        }
    }
}
