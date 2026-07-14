using System.Diagnostics;
using System.Management;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace NVMeDriverPatcher.Services;

public class ControllerAudit
{
    public string InstanceId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string BoundDriver { get; set; } = string.Empty;
    public string BoundDriverVersion { get; set; } = string.Empty;
    public bool IsNative { get; set; }
    public string QueueDepth { get; set; } = "Unknown";
    public string Firmware { get; set; } = string.Empty;

    // PnP driver-method evidence (RD P1). When nvmedisk.sys is bound with no patch
    // breadcrumbs, these fields let a reader tell an official rollout from a forced
    // "driver method" install: a forced install shows a non-Microsoft INF/provider or a
    // GenNvmeDisk compatible ID that the inbox stack would not have matched on its own.
    public string InfName { get; set; } = string.Empty;
    public string DriverProvider { get; set; } = string.Empty;
    public string DeviceClass { get; set; } = string.Empty;
    public string HardwareId { get; set; } = string.Empty;
    public string CompatibleId { get; set; } = string.Empty;
    public List<string> HardwareIds { get; set; } = [];
    public List<string> CompatibleIds { get; set; } = [];
    public DateTimeOffset ObservedAtUtc { get; set; }
    public string DriverCandidateCommand { get; set; } = string.Empty;
    public bool DriverCandidateProbeSucceeded { get; set; }
    public string DriverCandidateProbeError { get; set; } = string.Empty;
    public List<ControllerDriverCandidate> DriverCandidates { get; set; } = [];
}

public sealed class ControllerDriverCandidate
{
    public string InfName { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string ClassGuid { get; set; } = string.Empty;
    public string DriverVersion { get; set; } = string.Empty;
    public string SignerName { get; set; } = string.Empty;
    public string MatchingDeviceId { get; set; } = string.Empty;
    public string Rank { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsBestRanked => Status.Contains("BestRanked", StringComparison.OrdinalIgnoreCase);
    public bool IsInstalled => Status.Contains("Installed", StringComparison.OrdinalIgnoreCase);
}

internal sealed record PnpUtilDriverQueryResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    string? ExecutionError = null)
{
    public bool Success => ExitCode == 0 && string.IsNullOrWhiteSpace(ExecutionError);
}

public class PerControllerAuditReport
{
    public List<ControllerAudit> Controllers { get; set; } = new();
    public int NativeCount => Controllers.Count(c => c.IsNative);
    public int LegacyCount => Controllers.Count(c => !c.IsNative);
    public int CandidateProbeFailureCount => Controllers.Count(c => !c.DriverCandidateProbeSucceeded);
    public string Summary { get; set; } = string.Empty;
    public DateTimeOffset ObservedAtUtc { get; set; }

    /// <summary>
    /// Pure renderer for support-bundle PnP evidence (RD P1). For every native
    /// (nvmedisk.sys-bound) controller it prints the INF, driver provider, device class, and
    /// hardware/compatible IDs so a triager can distinguish Microsoft's official rollout from a
    /// forced Device Manager/PnPUtil "driver method" install. Returns a short "no native
    /// controllers" line when nothing is bound to the native stack.
    /// </summary>
    public string RenderForcedDriverEvidence()
    {
        var native = Controllers.Where(c => c.IsNative).ToList();
        if (native.Count == 0)
            return "No nvmedisk.sys-bound controllers — no forced-driver evidence to capture.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("nvmedisk.sys is bound on the controllers below. A Microsoft INF/provider with");
        sb.AppendLine("no patch breadcrumbs indicates the official rollout; a non-Microsoft provider or a");
        sb.AppendLine("manually-matched compatible ID indicates a forced 'driver method' install that must");
        sb.AppendLine("be rolled back in Device Manager or with pnputil after collecting driver-store evidence.");
        sb.AppendLine("Evidence command: pnputil /enum-drivers /files");
        foreach (var c in native)
        {
            sb.AppendLine($"  {c.FriendlyName}  (id={c.InstanceId})");
            sb.AppendLine($"    INF        : {Blankable(c.InfName)}");
            sb.AppendLine($"    Provider   : {Blankable(c.DriverProvider)}");
            sb.AppendLine($"    Version    : {Blankable(c.BoundDriverVersion)}");
            sb.AppendLine($"    Class      : {Blankable(c.DeviceClass)}");
            sb.AppendLine($"    HardwareID : {Blankable(JoinIds(c.HardwareIds, c.HardwareId))}");
            sb.AppendLine($"    CompatID   : {Blankable(JoinIds(c.CompatibleIds, c.CompatibleId))}");
        }
        return sb.ToString().TrimEnd();
    }

    public string RenderDriverCandidateEvidence()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Observed UTC: {ObservedAtUtc:o}");
        foreach (var controller in Controllers)
        {
            sb.AppendLine($"  {controller.FriendlyName}  (id={controller.InstanceId})");
            sb.AppendLine($"    Bound: inf={Blankable(controller.InfName)} provider={Blankable(controller.DriverProvider)} version={Blankable(controller.BoundDriverVersion)} driver={Blankable(controller.BoundDriver)}");
            sb.AppendLine($"    Query: {Blankable(controller.DriverCandidateCommand)}");
            if (!controller.DriverCandidateProbeSucceeded)
            {
                sb.AppendLine($"    Candidate error: {Blankable(controller.DriverCandidateProbeError)}");
                continue;
            }
            if (controller.DriverCandidates.Count == 0)
            {
                sb.AppendLine("    Candidates: none reported");
                continue;
            }
            foreach (var candidate in controller.DriverCandidates)
            {
                sb.AppendLine($"    Candidate: rank={Blankable(candidate.Rank)} inf={Blankable(candidate.InfName)} provider={Blankable(candidate.Provider)} version={Blankable(candidate.DriverVersion)} match={Blankable(candidate.MatchingDeviceId)} status={Blankable(candidate.Status)}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Blankable(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(unknown)" : value;

    private static string JoinIds(IReadOnlyCollection<string> allIds, string primary)
    {
        IEnumerable<string> ids = allIds.Count > 0
            ? allIds
            : string.IsNullOrWhiteSpace(primary) ? Array.Empty<string>() : new[] { primary };
        return string.Join("; ", ids.Where(id => !string.IsNullOrWhiteSpace(id)));
    }
}

// Per-controller version of PatchVerificationService. Enumerates every NVMe PnP instance
// and reports which driver is bound at that instance, so a user with multiple NVMe drives
// can see exactly which ones migrated and which didn't. Closes ROADMAP §2.1.
public static class PerControllerAuditService
{
    private const int MaxPnPUtilXmlCharacters = 4 * 1024 * 1024;
    private const int MaxDriverCandidatesPerController = 512;

    public static PerControllerAuditReport Audit()
    {
        var report = new PerControllerAuditReport { ObservedAtUtc = DateTimeOffset.UtcNow };

        try
        {
            // Win32_PnPSignedDriver gives us DriverName + InfName + DeviceID for storage
            // controllers. Filter on DeviceClass=SCSIAdapter|DiskDrive for both NVMe and SCSI.
            using var search = new ManagementObjectSearcher(
                "SELECT DeviceID, FriendlyName, DriverName, InfName, DriverVersion, DeviceClass, " +
                "DriverProviderName, HardWareID, CompatID " +
                "FROM Win32_PnPSignedDriver " +
                "WHERE DeviceClass='SCSIAdapter' OR DeviceClass='DiskDrive'");
            using var collection = WmiQueryHelper.ExecuteWithTimeout(search);
            foreach (var raw in collection)
            {
                if (raw is not ManagementObject mo) continue;
                using (mo)
                {
                    var devId = (mo["DeviceID"] as string) ?? string.Empty;
                    var driver = (mo["DriverName"] as string) ?? string.Empty;
                    var inf = (mo["InfName"] as string) ?? string.Empty;
                    var name = (mo["FriendlyName"] as string) ?? string.Empty;
                    if (string.IsNullOrEmpty(driver)) continue;

                    var isNvme = driver.IndexOf("nvme", StringComparison.OrdinalIgnoreCase) >= 0
                              || inf.IndexOf("nvme", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isNvme) continue;

                    var hardwareIds = AllOf(mo["HardWareID"]);
                    var compatibleIds = AllOf(mo["CompatID"]);

                    report.Controllers.Add(new ControllerAudit
                    {
                        InstanceId = devId,
                        FriendlyName = name,
                        BoundDriver = driver,
                        BoundDriverVersion = (mo["DriverVersion"] as string) ?? string.Empty,
                        IsNative = driver.IndexOf("nvmedisk", StringComparison.OrdinalIgnoreCase) >= 0,
                        InfName = inf,
                        DriverProvider = (mo["DriverProviderName"] as string) ?? string.Empty,
                        DeviceClass = (mo["DeviceClass"] as string) ?? string.Empty,
                        // HardWareID / CompatID come back as string[] in WMI; take the primary entry.
                        HardwareId = hardwareIds.FirstOrDefault() ?? string.Empty,
                        CompatibleId = compatibleIds.FirstOrDefault() ?? string.Empty,
                        HardwareIds = hardwareIds,
                        CompatibleIds = compatibleIds,
                        ObservedAtUtc = report.ObservedAtUtc,
                    });
                }
            }
        }

        catch
        {
            // WMI refusal: return whatever we managed to get.
        }

        Parallel.ForEach(
            report.Controllers,
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            controller => PopulateDriverCandidates(
                controller,
                QueryDriverCandidates(controller.InstanceId)));

        report.Summary = report.Controllers.Count == 0
            ? "No NVMe controllers detected (or WMI query denied)."
            : $"NVMe controllers: {report.NativeCount} native, {report.LegacyCount} legacy." +
              (report.CandidateProbeFailureCount == 0
                  ? " Driver candidate/rank evidence captured for every controller."
                  : $" Driver candidate/rank evidence unavailable for {report.CandidateProbeFailureCount} controller(s).");
        return report;
    }

    internal static void PopulateDriverCandidates(
        ControllerAudit controller,
        PnpUtilDriverQueryResult query)
    {
        controller.DriverCandidateCommand =
            $"pnputil.exe /enum-devices /instanceid \"{controller.InstanceId}\" /drivers /format xml";
        controller.DriverCandidates = [];
        if (!query.Success)
        {
            controller.DriverCandidateProbeSucceeded = false;
            controller.DriverCandidateProbeError = BuildQueryError(query);
            return;
        }

        try
        {
            controller.DriverCandidates = ParseDriverCandidatesXml(
                query.StandardOutput,
                controller.InstanceId);
            controller.DriverCandidateProbeSucceeded = true;
            controller.DriverCandidateProbeError = string.Empty;
        }
        catch (Exception ex)
        {
            controller.DriverCandidateProbeSucceeded = false;
            controller.DriverCandidateProbeError =
                $"PnPUtil candidate output could not be parsed ({ex.GetType().Name}: {ex.Message}).";
        }
    }

    internal static List<ControllerDriverCandidate> ParseDriverCandidatesXml(
        string xml,
        string expectedInstanceId)
    {
        if (string.IsNullOrWhiteSpace(xml))
            throw new InvalidDataException("PnPUtil returned empty XML.");
        if (xml.Length > MaxPnPUtilXmlCharacters)
            throw new InvalidDataException("PnPUtil XML exceeds the supported size limit.");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxPnPUtilXmlCharacters,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        if (document.Root?.Name.LocalName != "PnpUtil")
            throw new InvalidDataException("PnPUtil XML root is missing.");
        var devices = document.Root?.Elements("Device")
            .Where(device => string.Equals(
                (string?)device.Attribute("InstanceId"),
                expectedInstanceId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        if (devices.Length != 1)
            throw new InvalidDataException(
                $"Expected one exact device record; found {devices.Length}.");

        var candidateElements = devices[0].Element("MatchingDrivers")?.Elements("DriverName").ToArray()
                                ?? [];
        if (candidateElements.Length > MaxDriverCandidatesPerController)
            throw new InvalidDataException(
                $"PnPUtil returned {candidateElements.Length} candidates; limit is {MaxDriverCandidatesPerController}.");

        var candidates = candidateElements.Select(candidate => new ControllerDriverCandidate
            {
                InfName = (string?)candidate.Attribute("DriverName") ?? string.Empty,
                Provider = Element(candidate, "ProviderName"),
                ClassName = Element(candidate, "ClassName"),
                ClassGuid = Element(candidate, "ClassGuid"),
                DriverVersion = Element(candidate, "DriverVersion"),
                SignerName = Element(candidate, "SignerName"),
                MatchingDeviceId = Element(candidate, "MatchingDeviceId"),
                Rank = Element(candidate, "Rank"),
                Status = Element(candidate, "Status")
            }).ToList();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.InfName) ||
                !ulong.TryParse(candidate.Rank, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
                throw new InvalidDataException(
                    "PnPUtil returned a candidate without a valid INF name and hexadecimal rank.");
        }
        return candidates
            .OrderBy(candidate => ParseRank(candidate.Rank))
            .ThenBy(candidate => candidate.InfName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static PnpUtilDriverQueryResult QueryDriverCandidates(string instanceId)
    {
        try
        {
            var startInfo = new ProcessStartInfo("pnputil.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in new[]
                     {
                         "/enum-devices", "/instanceid", instanceId, "/drivers", "/format", "xml"
                     })
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("PnPUtil did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { process.WaitForExit(5_000); } catch { }
                Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
                return new(null, stdout.Result, stderr.Result, "PnPUtil timed out after 15 seconds.");
            }
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            return new(process.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Exception ex)
        {
            return new(null, string.Empty, string.Empty,
                $"PnPUtil execution failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    private static string BuildQueryError(PnpUtilDriverQueryResult query)
    {
        if (!string.IsNullOrWhiteSpace(query.ExecutionError)) return query.ExecutionError;
        var output = string.IsNullOrWhiteSpace(query.StandardError)
            ? query.StandardOutput
            : query.StandardError;
        output = string.Join(' ', output.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (output.Length > 512) output = output[..512] + "...";
        return $"PnPUtil exited {query.ExitCode?.ToString() ?? "without an exit code"}: " +
               (string.IsNullOrWhiteSpace(output) ? "no error text" : output);
    }

    private static string Element(XElement parent, string name) =>
        parent.Element(name)?.Value?.Trim() ?? string.Empty;

    private static ulong ParseRank(string rank) =>
        ulong.TryParse(rank, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : ulong.MaxValue;

    internal static List<ControllerAudit> FindCustomNativeWorkaroundEvidence(IEnumerable<ControllerAudit> controllers) =>
        controllers.Where(HasCustomNativeWorkaroundEvidence).ToList();

    internal static bool HasCustomNativeWorkaroundEvidence(ControllerAudit controller)
    {
        if (!MentionsNativeDriver(controller))
            return false;

        return HasNonMicrosoftNativeInf(controller) || HasScsiDiskNvmeCustomMatch(controller);
    }

    internal static bool HasNonMicrosoftNativeInf(ControllerAudit controller)
    {
        var oemInf = controller.InfName.StartsWith("oem", StringComparison.OrdinalIgnoreCase) &&
                     controller.InfName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase);
        var nonMicrosoftProvider =
            !string.IsNullOrWhiteSpace(controller.DriverProvider) &&
            controller.DriverProvider.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) < 0;
        return oemInf || nonMicrosoftProvider;
    }

    internal static bool HasScsiDiskNvmeCustomMatch(ControllerAudit controller) =>
        AllControllerIds(controller).Any(id =>
            id.StartsWith(@"SCSI\DiskNVMe____", StringComparison.OrdinalIgnoreCase));

    private static bool MentionsNativeDriver(ControllerAudit controller) =>
        controller.IsNative ||
        controller.BoundDriver.IndexOf("nvmedisk", StringComparison.OrdinalIgnoreCase) >= 0 ||
        controller.InfName.IndexOf("nvmedisk", StringComparison.OrdinalIgnoreCase) >= 0;

    private static IEnumerable<string> AllControllerIds(ControllerAudit controller)
    {
        foreach (var id in IdsOrPrimary(controller.HardwareIds, controller.HardwareId))
            if (!string.IsNullOrWhiteSpace(id)) yield return id;
        foreach (var id in IdsOrPrimary(controller.CompatibleIds, controller.CompatibleId))
            if (!string.IsNullOrWhiteSpace(id)) yield return id;
    }

    private static IEnumerable<string> IdsOrPrimary(IReadOnlyCollection<string> ids, string primary) =>
        ids.Count > 0
            ? ids
            : string.IsNullOrWhiteSpace(primary) ? Array.Empty<string>() : new[] { primary };

    // WMI string-array properties (HardWareID, CompatID) arrive as string[]; some providers
    // return a bare string.
    private static List<string> AllOf(object? value) => value switch
    {
        string[] arr => arr.Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
        string s when !string.IsNullOrWhiteSpace(s) => [s],
        _ => [],
    };
}
