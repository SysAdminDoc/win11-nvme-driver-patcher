namespace NVMeDriverPatcher.Services;

/// <summary>
/// Resolves Windows-supplied tools to absolute paths instead of letting them resolve through PATH.
/// </summary>
/// <remarks>
/// <para>
/// Both shipped executables carry a <c>requireAdministrator</c> manifest, so anything this process
/// launches runs elevated. Passing a bare tool name to <c>ProcessStartInfo</c> resolves it through
/// PATH, and the first match wins — a planted <c>fsutil.exe</c> in any earlier PATH entry would run with
/// administrator rights. The same shadowing already caused a real defect in the generated recovery
/// kit, where a bare <c>find</c> resolved to Git for Windows' GNU <c>find.exe</c> and hung the
/// integrity gate instead of counting files.
/// </para>
/// <para>
/// <see cref="Environment.SpecialFolder.System"/> is used rather than a hardcoded
/// <c>%SystemRoot%\System32</c> so the path stays correct under WOW64 file-system redirection and
/// on ARM64, where System32 holds the native-architecture binaries.
/// </para>
/// </remarks>
public static class SystemToolPathService
{
    /// <summary>
    /// Absolute path to the System32 directory for this process's architecture.
    /// </summary>
    public static string SystemDirectory => Environment.GetFolderPath(Environment.SpecialFolder.System);

    /// <summary>
    /// Resolves a System32-resident tool (for example <c>fsutil.exe</c>) to an absolute path.
    /// </summary>
    /// <remarks>
    /// Falls back to the bare name only when the System32 directory cannot be determined, which
    /// keeps behavior no worse than before on a system where the lookup fails.
    /// </remarks>
    public static string Resolve(string toolFileName)
    {
        if (string.IsNullOrWhiteSpace(toolFileName))
            throw new ArgumentException("A tool file name is required.", nameof(toolFileName));

        var systemDirectory = SystemDirectory;
        return string.IsNullOrWhiteSpace(systemDirectory)
            ? toolFileName
            : Path.Combine(systemDirectory, toolFileName);
    }

    /// <summary>
    /// Absolute path to Windows PowerShell 5.1, which lives under System32 rather than in it.
    /// </summary>
    public static string PowerShell =>
        Path.Combine(SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
}
