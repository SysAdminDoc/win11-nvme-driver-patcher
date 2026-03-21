namespace NVMeDriverPatcher.Models;

public class PatchSnapshot
{
    public string Timestamp { get; set; } = string.Empty;
    public PatchStatus Status { get; set; } = new();
    public string DriverActive { get; set; } = "Unknown";
    public bool BypassIO { get; set; }
    public Dictionary<string, string> Components { get; set; } = [];
}

public class PatchStatus
{
    public bool Applied { get; set; }
    public bool Partial { get; set; }
    public int Count { get; set; }
    public int Total { get; set; } = AppConfig.TotalComponents;
    public List<string> Keys { get; set; } = [];
}
