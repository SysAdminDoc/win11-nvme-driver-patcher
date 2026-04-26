using NVMeDriverPatcher.Models;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.ViewModels;

// Guidance / workflow partial of MainViewModel. Responsible for the right-rail narrative —
// the change plan, the action-readiness copy, the three-stage workflow guide, and the
// recommended-next-step primary/secondary buttons. Each method is pure state-projection:
// reads from `_preflight`, `Config`, and the patch-status dictionary, then writes into the
// [ObservableProperty]-generated fields declared in the main file. Split out so the
// end-user copy (which dominates this cluster) stays easy to audit without scrolling past
// 1,000 lines of unrelated command handlers.
public partial class MainViewModel
{
    private void UpdateChangePlan(PatchStatus? knownStatus = null)
    {
        ChangePlanSteps.Clear();

        void AddStep(string title, string detail, string toneColor)
        {
            ChangePlanSteps.Add(new ChangePlanStepVM
            {
                Title = title,
                Detail = detail,
                ToneColor = toneColor
            });
        }

        if (_preflight is null)
        {
            HasChangePlanSteps = false;
            ChangePlanSummaryText = "The machine-specific change plan will appear here after readiness checks finish.";
            return;
        }

        var patchStatus = knownStatus ?? RegistryService.GetPatchStatus();
        int plannedComponentCount = GetPlannedComponentCount();
        bool nativeDriverActive = _preflight.NativeNVMeStatus?.IsActive == true;

        if (CriticalCount > 0)
        {
            ChangePlanSummaryText = "The plan is paused until blocking checks are cleared. No registry change should be staged on this machine yet.";
            AddStep(
                "Resolve the blocking checks",
                "Start with the failing readiness items and the compatibility highlights. This machine still has one or more hard stops that make patching unsafe or incomplete.",
                "Red");
            AddStep(
                "Refresh the readiness scan",
                "Run the checks again after you address the blockers so the app can rebuild the migration plan from a clean state.",
                "Yellow");
            AddStep(
                "Document the baseline only after the scan clears",
                "Once the blockers are gone, capture a backup or benchmark baseline before treating the patch as a routine change.",
                "Accent");
        }
        else if (patchStatus.Partial)
        {
            ChangePlanSummaryText = "This machine is already in a partial patch state. The next step is to normalize that state before relying on a reboot.";
            AddStep(
                "Repair or remove the partial patch",
                "Use the existing controls to finish the missing registry writes or revert them so the system is not left in an ambiguous state.",
                "Red");
            AddStep(
                "Rebuild recovery evidence",
                "Refresh the recovery kit, verification script, and diagnostics after the partial state is resolved so the local support trail matches reality.",
                "Yellow");
            AddStep(
                "Re-run readiness before the next reboot",
                "A clean readiness pass is the best signal that the machine is safe to restart into the intended driver path.",
                "Accent");
        }
        else if (nativeDriverActive)
        {
            ChangePlanSummaryText = "Native NVMe is already active, so the remaining work is validation and long-term rollback hygiene rather than staging another driver change.";
            AddStep(
                "Verify the live migration",
                HasVerificationScript
                    ? "Open the verification script or review the current driver path to confirm the expected registry keys and Safe Mode protections are still present."
                    : "Generate the verification script so post-migration checks are easy to repeat or hand off.",
                "Green");
            AddStep(
                "Capture machine-specific evidence",
                HasBenchmarkHistory
                    ? "Review the benchmark comparison and telemetry trend so the outcome is supported by this machine's own evidence."
                    : "Run a validation benchmark and review telemetry so the final state is backed by more than a status label.",
                "Accent");
            AddStep(
                "Keep rollback material current",
                HasRecoveryKit && HasDiagnosticsReport
                    ? "Recovery assets and diagnostics are already present. Refresh them only when you want a newer support bundle."
                    : "Export the recovery kit and diagnostics so a future rollback or support handoff does not depend on memory.",
                "Yellow");
        }
        else if (patchStatus.Applied)
        {
            ChangePlanSummaryText = "The registry changes are already staged. The remaining plan is about reboot, verification, and final proof that the driver path actually changed.";
            AddStep(
                "Restart to activate nvmedisk.sys",
                _preflight.BitLockerEnabled
                    ? "Windows still needs a reboot to switch driver paths, and BitLocker has already been prepared to avoid an unnecessary recovery-key interruption."
                    : "Windows still needs a reboot to switch from the legacy path to the native driver. Nothing meaningful changes until that restart finishes.",
                "Yellow");
            AddStep(
                "Run verification and review recovery assets",
                HasVerificationScript && HasRecoveryKit
                    ? "The verification script and rollback kit are already available, so the post-reboot safety path is in place."
                    : "Before you walk away, make sure the verification script and recovery kit are ready so the reboot path stays calm if anything looks off.",
                "Accent");
            AddStep(
                "Validate the outcome with local evidence",
                HasBenchmarkHistory
                    ? "Return after reboot to compare benchmarks, review telemetry, and export diagnostics if you want a support-ready final record."
                    : "After reboot, run a benchmark or export diagnostics so the migration result is documented on this exact machine.",
                "Green");
        }
        else
        {
            ChangePlanSummaryText = "Applying on this machine will stage the native NVMe path, create local rollback evidence, and require a reboot before the live driver actually changes.";
            AddStep(
                "Document the current baseline",
                HasBackupFiles || HasBenchmarkHistory
                    ? "You already have some local evidence. Refresh the backup or capture a new benchmark if you want a stronger before-state record."
                    : "Create a registry backup and optionally run a benchmark so the current state is easy to compare or restore later.",
                "Accent");
            AddStep(
                "Stage the driver transition",
                IncludeServerKey
                    ? $"The apply action will write {plannedComponentCount} patch components, including the optional Server 2025 key, plus Safe Mode protections and local snapshots."
                    : $"The apply action will write {plannedComponentCount} core patch components, add Safe Mode protections, and save local snapshots before and after the change.",
                "Green");
            AddStep(
                "Prepare for reboot and follow-up validation",
                _preflight.BitLockerEnabled
                    ? "BitLocker will be suspended for one reboot, recovery assets will be refreshed, and you should return after restart to confirm the native path is active."
                    : "Recovery assets will be refreshed, then a reboot is required. After restart, verify the migration and capture final evidence through telemetry or diagnostics.",
                "Yellow");
        }

        HasChangePlanSteps = ChangePlanSteps.Count > 0;
    }

    private void UpdateActionGuidance(PatchStatus? knownStatus = null)
    {
        if (_preflight is null)
        {
            ActionReadinessText = "Readiness checks will explain when apply or remove becomes available.";
            ActionReadinessColor = "TextMuted";
            ApplyButtonTooltipText = "Readiness checks are still running.";
            RemoveButtonTooltipText = RemoveUnavailableText;
            return;
        }

        var status = knownStatus ?? RegistryService.GetPatchStatus();
        int plannedComponentCount = GetPlannedComponentCount();

        if (CriticalCount > 0)
        {
            string blockerSummary = BuildBlockingActionSummary();
            ActionReadinessText = $"Apply is blocked until the critical checks are resolved. {blockerSummary}";
            ActionReadinessColor = "Red";
            ApplyButtonTooltipText = ActionReadinessText;
            RemoveButtonTooltipText = status.Applied || status.Partial
                ? "Remove can still revert the staged or partial registry state."
                : RemoveUnavailableText;
            return;
        }

        if (_preflight.NativeNVMeStatus?.IsActive == true && !status.Applied && !status.Partial)
        {
            ActionReadinessText = "Native NVMe is already active. Apply is optional here and mainly helps stage the registry keys, Safe Mode protections, and recovery material for consistency.";
            ActionReadinessColor = "Green";
            ApplyButtonTooltipText = $"Apply will stage {plannedComponentCount} patch components plus recovery helpers, even though the native driver path is already live.";
            RemoveButtonTooltipText = RemoveUnavailableText;
            return;
        }

        if (status.Applied)
        {
            ActionReadinessText = "Reinstall refreshes the staged registry set and recovery assets. Remove deletes the patch keys and returns the machine to the legacy path after the next reboot.";
            ActionReadinessColor = "Yellow";
            ApplyButtonTooltipText = $"Reinstall will refresh {plannedComponentCount} patch components, Safe Mode protections, and local snapshots.";
            RemoveButtonTooltipText = "Remove clears the staged patch keys and Safe Mode protections, then requires a reboot to restore the legacy path.";
            return;
        }

        if (status.Partial)
        {
            ActionReadinessText = "This machine is in a partial patch state. Repair is available to complete the missing writes, and Remove can also cleanly revert the partial state.";
            ActionReadinessColor = "Yellow";
            ApplyButtonTooltipText = $"Repair will attempt to complete the intended {plannedComponentCount} patch components and refresh recovery helpers.";
            RemoveButtonTooltipText = "Remove can clean up the partial registry state before you retry the migration.";
            return;
        }

        if (WarningCount > 0)
        {
            ActionReadinessText = "Apply is available, but this machine still has advisory notes. Review Compatibility Highlights and the machine impact plan before you commit.";
            ActionReadinessColor = "Yellow";
            ApplyButtonTooltipText = $"Apply will stage {plannedComponentCount} patch components, Safe Mode protections, and local recovery material once you accept the advisory tradeoffs.";
            RemoveButtonTooltipText = RemoveUnavailableText;
            return;
        }

        ActionReadinessText = $"Apply is ready. It will stage {plannedComponentCount} patch components, add Safe Mode protections, save snapshots, and require a reboot before the live driver path changes.";
        ActionReadinessColor = "Green";
        ApplyButtonTooltipText = ActionReadinessText;
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
            _ => key
        };
    }

    private void UpdateWorkflowGuide(PatchStatus? knownStatus = null)
    {
        var patchStatus = knownStatus ?? RegistryService.GetPatchStatus();
        bool nativeDriverActive = _preflight?.NativeNVMeStatus?.IsActive == true;
        bool prepEvidenceReady = HasBackupFiles || HasBenchmarkHistory || HasRecoveryKit;
        bool prepReady = CriticalCount == 0 && HasBackupFiles && (HasBenchmarkHistory || HasRecoveryKit);
        bool validationEvidenceReady = HasBenchmarkHistory || HasDiagnosticsReport;

        if (CriticalCount > 0)
        {
            PreparationStageStateText = "Blocked by readiness checks";
            PreparationStageDetailText = "Resolve the critical preflight issues first. Backups and baselines matter, but only after the system is allowed to proceed.";
            PreparationStageColor = "Red";
        }
        else if (prepReady)
        {
            PreparationStageStateText = "Prepared for a controlled change";
            PreparationStageDetailText = "Backups and baseline evidence are in place, so the patch can be staged with a much clearer rollback story.";
            PreparationStageColor = "Green";
        }
        else if (prepEvidenceReady)
        {
            PreparationStageStateText = "Partially prepared";
            PreparationStageDetailText = "You already captured some safety material. Add the missing backup or baseline pieces before treating this as a fully documented change.";
            PreparationStageColor = "Yellow";
        }
        else
        {
            PreparationStageStateText = "Capture baseline and safety artifacts";
            PreparationStageDetailText = "Start with a registry backup, optional pre-patch benchmark, and recovery materials so the change stays easy to explain and reverse.";
            PreparationStageColor = "Accent";
        }

        if (nativeDriverActive)
        {
            RestartStageStateText = "Migration is live";
            RestartStageDetailText = "Windows is already running on nvmedisk.sys, so the reboot phase is complete for this machine.";
            RestartStageColor = "Green";
        }
        else if (patchStatus.Applied)
        {
            RestartStageStateText = "Restart required";
            RestartStageDetailText = "The patch is staged, but Windows will stay on the legacy path until the next reboot actually completes.";
            RestartStageColor = "Yellow";
        }
        else if (patchStatus.Partial)
        {
            RestartStageStateText = "Staging is incomplete";
            RestartStageDetailText = "Some components are present, but the system is not in a clean restart-ready state yet. Repair or remove the partial patch first.";
            RestartStageColor = "Red";
        }
        else
        {
            RestartStageStateText = "Patch not staged yet";
            RestartStageDetailText = "Once the patch is applied, this phase will flip to a clear restart requirement instead of making you infer it from logs.";
            RestartStageColor = "TextDim";
        }

        if (nativeDriverActive && validationEvidenceReady && HasVerificationScript)
        {
            ValidationStageStateText = "Validated with local evidence";
            ValidationStageDetailText = "The native path is active and this machine has local proof through benchmarks, diagnostics, and verification materials.";
            ValidationStageColor = "Green";
        }
        else if (nativeDriverActive && (validationEvidenceReady || HasVerificationScript))
        {
            ValidationStageStateText = "Validation is in progress";
            ValidationStageDetailText = "The driver migration is live. Finish the proof trail with a benchmark comparison or diagnostics export so the outcome is easy to defend later.";
            ValidationStageColor = "Yellow";
        }
        else if (patchStatus.Applied)
        {
            ValidationStageStateText = "Available after reboot";
            ValidationStageDetailText = "Telemetry, benchmarks, and diagnostics become meaningful proof once Windows has restarted onto the native driver.";
            ValidationStageColor = "Accent";
        }
        else
        {
            ValidationStageStateText = "Validation comes after the driver changes";
            ValidationStageDetailText = "Use benchmarks, telemetry, and diagnostics after reboot to confirm the migration on this exact machine.";
            ValidationStageColor = "TextDim";
        }
    }

    private void UpdateRecommendedActions(PatchStatus? knownStatus = null)
    {
        void SetPrimary(string id, string text, bool enabled)
        {
            HasNextStepPrimaryAction = true;
            NextStepPrimaryActionId = id;
            NextStepPrimaryActionText = text;
            NextStepPrimaryActionEnabled = enabled;
        }

        void SetSecondary(string id, string text, bool enabled)
        {
            HasNextStepSecondaryAction = true;
            NextStepSecondaryActionId = id;
            NextStepSecondaryActionText = text;
            NextStepSecondaryActionEnabled = enabled;
        }

        HasNextStepPrimaryAction = false;
        NextStepPrimaryActionText = "";
        NextStepPrimaryActionId = "";
        NextStepPrimaryActionEnabled = false;
        HasNextStepSecondaryAction = false;
        NextStepSecondaryActionText = "";
        NextStepSecondaryActionId = "";
        NextStepSecondaryActionEnabled = false;

        if (IsLoading)
            return;

        var patchStatus = knownStatus ?? RegistryService.GetPatchStatus();
        bool nativeDriverActive = _preflight?.NativeNVMeStatus?.IsActive == true;

        if (CriticalCount > 0)
        {
            SetPrimary("refresh_checks", "Refresh Checks", ButtonsEnabled);
            SetSecondary(
                HasRecoveryKit || HasVerificationScript || HasDiagnosticsReport ? "open_recovery" : "open_activity",
                HasRecoveryKit || HasVerificationScript || HasDiagnosticsReport ? "Review Recovery" : "Open Activity",
                true);
            return;
        }

        if (nativeDriverActive)
        {
            if (HasBenchmarkHistory)
            {
                SetPrimary("open_telemetry", "Open Telemetry", true);
                SetSecondary(
                    HasDiagnosticsReport ? "open_recovery" : "export_diagnostics",
                    HasDiagnosticsReport ? "Review Recovery" : "Export Diagnostics",
                    HasDiagnosticsReport || ButtonsEnabled);
            }
            else
            {
                SetPrimary("run_benchmark", "Run Benchmark", ButtonsEnabled);
                SetSecondary("open_telemetry", "Open Telemetry", true);
            }

            return;
        }

        if (patchStatus.Applied)
        {
            SetPrimary("open_recovery", "Open Recovery", true);
            if (!HasDiagnosticsReport)
                SetSecondary("export_diagnostics", "Export Diagnostics", ButtonsEnabled);
            else if (!HasRecoveryKit)
                SetSecondary("create_recovery_kit", "Create Recovery Kit", ButtonsEnabled);
            else
                SetSecondary("open_activity", "Open Activity", true);
            return;
        }

        if (WarningCount > 0)
        {
            if (!HasBenchmarkHistory)
                SetPrimary("run_benchmark", "Capture Baseline", ButtonsEnabled);
            else if (!HasBackupFiles)
                SetPrimary("create_backup", "Create Backup", ButtonsEnabled);
            else
                SetPrimary("open_recovery", "Review Recovery", true);

            if (!HasBackupFiles && !HasBenchmarkHistory)
                SetSecondary("create_backup", "Create Backup", ButtonsEnabled);
            else if (!HasRecoveryKit)
                SetSecondary("create_recovery_kit", "Create Recovery Kit", ButtonsEnabled);
            else
                SetSecondary("open_activity", "Open Activity", true);
            return;
        }

        if (!HasBenchmarkHistory)
            SetPrimary("run_benchmark", "Run Benchmark", ButtonsEnabled);
        else if (!HasBackupFiles)
            SetPrimary("create_backup", "Create Backup", ButtonsEnabled);
        else if (!HasRecoveryKit)
            SetPrimary("create_recovery_kit", "Create Recovery Kit", ButtonsEnabled);
        else
            SetPrimary("apply_patch", "Apply Patch", ApplyEnabled);

        if (!HasBackupFiles && !HasBenchmarkHistory)
            SetSecondary("create_backup", "Create Backup", ButtonsEnabled);
        else if (!HasRecoveryKit)
            SetSecondary("open_recovery", "Open Recovery", true);
        else
            SetSecondary("open_benchmarks", "Review Benchmarks", true);
    }
}
