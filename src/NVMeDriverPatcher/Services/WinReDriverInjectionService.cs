using System.IO;
using System.Text;

namespace NVMeDriverPatcher.Services;

public sealed class DismStep
{
    public string Description { get; set; } = string.Empty;
    public string Exe { get; set; } = "dism.exe";
    public string[] Args { get; set; } = Array.Empty<string>();

    public string CommandLine => Exe + " " + string.Join(" ", Args);
}

public sealed class WinReInjectionPlan
{
    public string WinReImagePath { get; set; } = string.Empty;
    public string MountDir { get; set; } = string.Empty;
    public string DriverInfPath { get; set; } = string.Empty;
    public List<DismStep> Steps { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsExecutable { get; set; }
}

// Plans (and previews) injecting the legacy stornvme.sys driver into WinRE's boot image so the
// recovery environment can always mount the system volume even if the native NVMe stack wedges
// startup. This service is PREVIEW/PLAN-ONLY: it never mounts or mutates the image. It emits the
// exact DISM commands and blast-radius warnings so an operator can review and run them deliberately
// (the destructive mount/commit + a live WinRE boot test are a separate, hardware-validated step).
public static class WinReDriverInjectionService
{
    public static string DefaultStornvmeInf()
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return Path.Combine(windir, "INF", "stornvme.inf");
    }

    /// <summary>
    /// Pure: builds the ordered DISM plan (mount → add-driver → unmount/commit) plus blast-radius
    /// warnings. <paramref name="driverInfMissing"/>/<paramref name="imageMissing"/> let the caller
    /// pass probe results so the plan is marked non-executable with a clear reason rather than
    /// silently producing commands that would fail.
    /// </summary>
    public static WinReInjectionPlan BuildPlan(
        string winReImagePath, string mountDir, string driverInfPath,
        bool imageMissing = false, bool driverInfMissing = false)
    {
        var plan = new WinReInjectionPlan
        {
            WinReImagePath = winReImagePath,
            MountDir = mountDir,
            DriverInfPath = driverInfPath,
            IsExecutable = !imageMissing && !driverInfMissing
                           && !string.IsNullOrWhiteSpace(winReImagePath)
                           && !string.IsNullOrWhiteSpace(driverInfPath),
        };

        plan.Steps.Add(new DismStep
        {
            Description = "Mount the WinRE image read/write",
            Args = new[] { "/Mount-Image", $"/ImageFile:{winReImagePath}", "/Index:1", $"/MountDir:{mountDir}" },
        });
        plan.Steps.Add(new DismStep
        {
            Description = "Add the legacy stornvme driver to the mounted image",
            Args = new[] { $"/Image:{mountDir}", "/Add-Driver", $"/Driver:{driverInfPath}" },
        });
        plan.Steps.Add(new DismStep
        {
            Description = "Commit the change and unmount",
            Args = new[] { "/Unmount-Image", $"/MountDir:{mountDir}", "/Commit" },
        });

        if (imageMissing)
            plan.Warnings.Add($"WinRE image not found at '{winReImagePath}' — run reagentc /info to locate it (it may need reagentc /enable first).");
        if (driverInfMissing)
            plan.Warnings.Add($"Driver INF not found at '{driverInfPath}' — stornvme.inf ships in %WINDIR%\\INF on a healthy install.");

        plan.Warnings.Add("BLAST RADIUS: this mutates the recovery boot image. Back up the WinRE .wim first (copy it elsewhere).");
        plan.Warnings.Add("If a mount is interrupted, run 'Dism /Cleanup-Mountpoints' before retrying.");
        plan.Warnings.Add("After committing, boot into WinRE once and confirm the system volume is accessible BEFORE relying on it for recovery.");
        return plan;
    }

    /// <summary>Pure: renders the plan for the CLI/GUI preview (dry-run output).</summary>
    public static string RenderPlan(WinReInjectionPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WinRE stornvme injection — PLANNED DISM operations (preview only, nothing mutated):");
        sb.AppendLine($"  WinRE image : {plan.WinReImagePath}");
        sb.AppendLine($"  Driver INF  : {plan.DriverInfPath}");
        sb.AppendLine($"  Mount dir   : {plan.MountDir}");
        sb.AppendLine();
        int i = 1;
        foreach (var step in plan.Steps)
        {
            sb.AppendLine($"  {i++}. {step.Description}");
            sb.AppendLine($"     {step.CommandLine}");
        }
        sb.AppendLine();
        sb.AppendLine(plan.IsExecutable
            ? "Plan is runnable. Review the warnings, then run the commands above from an elevated prompt."
            : "Plan is NOT runnable as-is — resolve the warnings below first.");
        foreach (var w in plan.Warnings)
            sb.AppendLine($"  ! {w}");
        return sb.ToString().TrimEnd();
    }
}
