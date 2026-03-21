namespace NVMeDriverPatcher.Models;

public class SystemDrive
{
    public int Number { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public bool IsNVMe { get; set; }
    public string BusType { get; set; } = string.Empty;
    public bool IsBoot { get; set; }
    public string PNPDeviceID { get; set; } = string.Empty;
}

public class NVMeDriverDetails
{
    public bool HasThirdParty { get; set; }
    public string ThirdPartyName { get; set; } = string.Empty;
    public string InboxVersion { get; set; } = string.Empty;
    public string CurrentDriver { get; set; } = string.Empty;
    public string QueueDepth { get; set; } = "Unknown";
    public Dictionary<string, string> FirmwareVersions { get; set; } = [];
}

public class NativeNVMeStatus
{
    public bool IsActive { get; set; }
    public string ActiveDriver { get; set; } = "Unknown";
    public string DeviceCategory { get; set; } = "Unknown";
    public List<string> StorageDisks { get; set; } = [];
    public string Details { get; set; } = string.Empty;
}

public class BypassIOResult
{
    public bool Supported { get; set; }
    public string StorageType { get; set; } = "Unknown";
    public string DriverCompat { get; set; } = "Unknown";
    public string BlockedBy { get; set; } = string.Empty;
    public string RawOutput { get; set; } = string.Empty;
    public string Warning { get; set; } = string.Empty;
}

public class NVMeHealthInfo
{
    public string Temperature { get; set; } = "N/A";
    public string Wear { get; set; } = "N/A";
    public int MediaErrors { get; set; }
    public string HealthStatus { get; set; } = "Unknown";
    public string OperationalStatus { get; set; } = "Unknown";
    public string PowerOnHours { get; set; } = "N/A";
    public int ReadErrors { get; set; }
    public int WriteErrors { get; set; }
    public string AvailableSpare { get; set; } = "N/A";
    public string SmartTooltip { get; set; } = string.Empty;
}

public class StorageMigrationResult
{
    public List<string> Migrated { get; set; } = [];
    public List<string> Legacy { get; set; } = [];
}

public class IncompatibleSoftwareInfo
{
    public string Name { get; set; } = string.Empty;
    public string Severity { get; set; } = "Low";
    public string Message { get; set; } = string.Empty;
}

public class WindowsBuildDetails
{
    public int BuildNumber { get; set; }
    public string DisplayVersion { get; set; } = "Unknown";
    public bool Is24H2OrLater { get; set; }
    public bool IsRecommended { get; set; }
    public string Caption { get; set; } = string.Empty;
    public int UBR { get; set; }
}
