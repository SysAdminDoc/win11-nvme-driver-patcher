using System.IO;

namespace NVMeDriverPatcher.Services;

public sealed class PolicyFileCopy
{
    public string Source { get; set; } = string.Empty;
    public string Dest { get; set; } = string.Empty;
}

public sealed class PolicyInstallPlan
{
    public List<PolicyFileCopy> Copies { get; } = new();
    public List<string> Warnings { get; } = new();
}

// Installs the ADMX/ADML Group Policy templates into the local PolicyDefinitions store (or a
// domain Central Store path) so admins don't have to hand-copy files. The .admx lands in the
// PolicyDefinitions root and each .adml lands in the matching language subfolder (en-US, de-DE…).
public static class PolicyTemplateInstallService
{
    /// <summary>Where the bundled templates live next to the installed app (MSI lays them down
    /// under an <c>admx\</c> subfolder; a flat layout beside the exe is also accepted).</summary>
    public static string DefaultSourceDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var admxSub = Path.Combine(baseDir, "admx");
        return Directory.Exists(admxSub) ? admxSub : baseDir;
    }

    /// <summary>The local machine policy store. Pass a Central Store path for domain deployment.</summary>
    public static string DefaultPolicyDefinitionsDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "PolicyDefinitions");

    /// <summary>
    /// Pure: maps every .admx (→ PolicyDefinitions root) and .adml (→ PolicyDefinitions\&lt;lang&gt;)
    /// under <paramref name="sourceDir"/> to its destination. Language folders are discovered from
    /// the immediate subdirectories that contain .adml files, so en-US/de-DE/etc. land correctly.
    /// </summary>
    public static PolicyInstallPlan BuildPlan(string sourceDir, string policyDefinitionsDir)
    {
        var plan = new PolicyInstallPlan();
        if (!Directory.Exists(sourceDir))
        {
            plan.Warnings.Add($"Template source directory not found: {sourceDir}");
            return plan;
        }

        foreach (var admx in Directory.EnumerateFiles(sourceDir, "*.admx", SearchOption.TopDirectoryOnly))
            plan.Copies.Add(new PolicyFileCopy { Source = admx, Dest = Path.Combine(policyDefinitionsDir, Path.GetFileName(admx)) });

        foreach (var langDir in Directory.EnumerateDirectories(sourceDir))
        {
            var lang = Path.GetFileName(langDir);
            foreach (var adml in Directory.EnumerateFiles(langDir, "*.adml", SearchOption.TopDirectoryOnly))
                plan.Copies.Add(new PolicyFileCopy { Source = adml, Dest = Path.Combine(policyDefinitionsDir, lang, Path.GetFileName(adml)) });
        }

        if (plan.Copies.Count == 0)
            plan.Warnings.Add($"No .admx/.adml templates found under {sourceDir}.");
        return plan;
    }

    public static (bool Success, string Summary) Install(string sourceDir, string policyDefinitionsDir, Action<string>? log = null)
    {
        var plan = BuildPlan(sourceDir, policyDefinitionsDir);
        foreach (var w in plan.Warnings) log?.Invoke($"  [WARN] {w}");
        if (plan.Copies.Count == 0)
            return (false, "No policy templates were found to install.");

        int copied = 0;
        try
        {
            foreach (var c in plan.Copies)
            {
                var destDir = Path.GetDirectoryName(c.Dest);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(c.Source, c.Dest, overwrite: true);
                log?.Invoke($"  [OK] {Path.GetFileName(c.Dest)} -> {c.Dest}");
                copied++;
            }
        }
        catch (Exception ex)
        {
            return (false, $"Policy install failed after {copied} file(s): {ex.Message}");
        }
        return (true, $"Installed {copied} policy template file(s) into {policyDefinitionsDir}.");
    }

    public static (bool Success, string Summary) Uninstall(string sourceDir, string policyDefinitionsDir, Action<string>? log = null)
    {
        // The uninstall set is exactly the install destinations, derived from the same source layout.
        var plan = BuildPlan(sourceDir, policyDefinitionsDir);
        int removed = 0;
        foreach (var c in plan.Copies)
        {
            try
            {
                if (File.Exists(c.Dest)) { File.Delete(c.Dest); removed++; log?.Invoke($"  [OK] removed {c.Dest}"); }
            }
            catch (Exception ex) { log?.Invoke($"  [WARN] could not remove {c.Dest}: {ex.Message}"); }
        }
        return (true, $"Removed {removed} policy template file(s) from {policyDefinitionsDir}.");
    }
}
