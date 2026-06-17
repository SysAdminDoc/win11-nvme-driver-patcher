namespace NVMeDriverPatcher.Models;

public enum CheckStatus
{
    Checking,
    Pass,
    Warning,
    Fail,
    Info
}

public class PreflightCheck
{
    public CheckStatus Status { get; set; } = CheckStatus.Checking;
    public string Message { get; set; } = "Checking…";
    public bool Critical { get; set; }

    public PreflightCheck() { }

    public PreflightCheck(CheckStatus status, string message, bool critical = false)
    {
        Status = status;
        Message = message;
        Critical = critical;
    }
}
