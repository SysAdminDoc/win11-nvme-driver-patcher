using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NVMeDriverPatcher.Services;

/// <summary>
/// Resolves which <c>HKLM\SYSTEM\ControlSet00N</c> hives the patch must be mirrored into.
///
/// <para>
/// <c>CurrentControlSet</c> is a boot-time volatile symlink to whichever control set the loader
/// selected. Windows keeps at least one spare — usually the LastKnownGood set — and boot recovery
/// (a scheduled <c>chkdsk /f</c>, a deleted <c>bootstat.dat</c>, or repeated failed boots) can
/// promote that spare. A spare cloned before the patch does not contain the patch's feature flags
/// or SafeBoot keys, so promotion silently rebinds the legacy stornvme driver — GitHub issue #15.
/// </para>
///
/// <para>
/// Writing the same values into every existing control set makes promotion a non-event. Sets are
/// only ever <em>enumerated</em>, never created: a control set that does not exist is not a boot
/// configuration Windows can promote, and fabricating one would be inventing boot state.
/// </para>
/// </summary>
public static class ControlSetService
{
    internal const string SystemHive = "SYSTEM";
    internal const string CurrentControlSetSegment = @"SYSTEM\CurrentControlSet\";
    private const string SelectKeyPath = @"SYSTEM\Select";

    private static readonly Regex ControlSetNamePattern =
        new(@"^ControlSet(\d{3})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Pure: the control sets a patch write must be mirrored into, in ascending numeric order.
    ///
    /// <para>
    /// The set <paramref name="currentControlSet"/> names is excluded — <c>CurrentControlSet</c>
    /// already resolves to it, so mirroring it would write the same physical key twice and add a
    /// duplicate ledger baseline entry for it. Names that are not <c>ControlSetNNN</c> are ignored
    /// (<c>ControlSet001</c> siblings such as <c>Setup</c> or <c>Select</c> live in the same hive).
    /// </para>
    ///
    /// <para>
    /// When <paramref name="currentControlSet"/> is null the current set is unknown, so nothing is
    /// excluded and every set is mirrored. That is deliberately the safe direction: writing the
    /// current set again is a harmless duplicate write, whereas skipping a set that is actually a
    /// spare would leave exactly the gap this exists to close.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> SelectMirrorTargets(
        IEnumerable<string>? existingControlSetNames,
        int? currentControlSet)
    {
        if (existingControlSetNames is null) return [];

        var targets = new List<(int Number, string Name)>();
        foreach (var name in existingControlSetNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var match = ControlSetNamePattern.Match(name.Trim());
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                continue;
            if (currentControlSet is not null && number == currentControlSet.Value) continue;
            if (targets.Any(existing => existing.Number == number)) continue;
            targets.Add((number, $"ControlSet{number:000}"));
        }

        return targets.OrderBy(entry => entry.Number).Select(entry => entry.Name).ToArray();
    }

    /// <summary>
    /// Pure: rewrites a <c>SYSTEM\CurrentControlSet\...</c> path to the same location under a
    /// specific control set. Returns null when the path is not under CurrentControlSet, so a
    /// caller cannot silently mirror something that was never control-set scoped.
    /// </summary>
    public static string? MirrorPath(string? currentControlSetPath, string controlSetName)
    {
        if (string.IsNullOrWhiteSpace(currentControlSetPath) || string.IsNullOrWhiteSpace(controlSetName))
            return null;
        if (!currentControlSetPath.StartsWith(CurrentControlSetSegment, StringComparison.OrdinalIgnoreCase))
            return null;
        var suffix = currentControlSetPath[CurrentControlSetSegment.Length..];
        if (string.IsNullOrEmpty(suffix)) return null;
        return $@"{SystemHive}\{controlSetName}\{suffix}";
    }

    /// <summary>Live enumeration of mirror targets. Returns an empty list when the hive is unreadable.</summary>
    public static IReadOnlyList<string> GetMirrorTargets()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var system = hklm.OpenSubKey(SystemHive);
            if (system is null) return [];
            return SelectMirrorTargets(system.GetSubKeyNames(), ReadCurrentControlSet(hklm));
        }
        catch
        {
            // Advisory: an unreadable SYSTEM hive means no mirroring, never a failed patch.
            return [];
        }
    }

    /// <summary>The number <c>SYSTEM\Select\Current</c> points at, or null when unreadable.</summary>
    public static int? ReadCurrentControlSet()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return ReadCurrentControlSet(hklm);
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadCurrentControlSet(RegistryKey hklm)
    {
        using var select = hklm.OpenSubKey(SelectKeyPath);
        return select?.GetValue("Current") as int?;
    }
}
