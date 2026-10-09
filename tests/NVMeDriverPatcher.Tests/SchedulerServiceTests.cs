using System.Security.AccessControl;
using NVMeDriverPatcher.Services;

namespace NVMeDriverPatcher.Tests;

public sealed class SchedulerServiceTests
{
    [Fact]
    public void BootVerifyArgs_RunAsSystemHighest_OnStart_WatchdogAutoRevert()
    {
        var args = SchedulerService.BuildBootVerifyArgs(@"C:\Tools\NVMeDriverPatcher.Cli.exe");

        Assert.Equal("/Create", args[0]);
        AssertPair(args, "/RU", "SYSTEM");
        AssertPair(args, "/RL", "HIGHEST");
        AssertPair(args, "/TN", SchedulerService.BootTaskName);
        AssertPair(args, "/SC", "ONSTART");
        AssertPair(args, "/TR", "\"C:\\Tools\\NVMeDriverPatcher.Cli.exe\" watchdog --auto-revert");
    }

    [Fact]
    public void WatchdogSweepArgs_ClampsIntervalAndUsesMinuteSchedule()
    {
        var tooSmall = SchedulerService.BuildWatchdogSweepArgs(@"cli.exe", 1);
        AssertPair(tooSmall, "/SC", "MINUTE");
        AssertPair(tooSmall, "/MO", "5");   // clamped up to the 5-minute floor

        var inRange = SchedulerService.BuildWatchdogSweepArgs(@"cli.exe", 30);
        AssertPair(inRange, "/MO", "30");
        AssertPair(inRange, "/TR", "\"cli.exe\" watchdog");

        var justUnderADay = SchedulerService.BuildWatchdogSweepArgs(@"cli.exe", 1439);
        AssertPair(justUnderADay, "/SC", "MINUTE");
        AssertPair(justUnderADay, "/MO", "1439");
    }

    /// <summary>
    /// schtasks.exe documents /SC MINUTE /MO as 1 - 1439. The 24-hour ceiling used to be emitted
    /// as "/SC MINUTE /MO 1440", which schtasks rejects ("The /MO value is invalid"), so asking
    /// for a daily sweep failed outright. A day or more is now a plain daily schedule.
    /// </summary>
    [Theory]
    [InlineData(1440)]
    [InlineData(1441)]
    [InlineData(99999)]
    [InlineData(int.MaxValue)]
    public void WatchdogSweepArgs_ADayOrMore_UsesDailySchedule(int intervalMinutes)
    {
        var args = SchedulerService.BuildWatchdogSweepArgs(@"cli.exe", intervalMinutes);

        AssertPair(args, "/SC", "DAILY");
        Assert.DoesNotContain("/MO", args);
        AssertPair(args, "/TN", SchedulerService.WatchdogTaskName);
        AssertPair(args, "/TR", "\"cli.exe\" watchdog");
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(60)]
    [InlineData(1439)]
    [InlineData(1440)]
    [InlineData(int.MaxValue)]
    public void WatchdogSweepArgs_NeverEmitAMinuteModifierSchtasksRejects(int intervalMinutes)
    {
        var args = SchedulerService.BuildWatchdogSweepArgs(@"cli.exe", intervalMinutes);
        var schedule = args[Array.IndexOf(args, "/SC") + 1];
        if (schedule != "MINUTE") return;

        var modifier = int.Parse(args[Array.IndexOf(args, "/MO") + 1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(modifier, 1, 1439);
    }

    [Fact]
    public void UnregisterArgs_DeletesNamedTask()
    {
        var args = SchedulerService.BuildUnregisterArgs(SchedulerService.WatchdogTaskName);
        Assert.Equal("/Delete", args[0]);
        Assert.Contains("/F", args);
        AssertPair(args, "/TN", SchedulerService.WatchdogTaskName);
    }

    // Both tasks run the CLI as SYSTEM, so the exe has to sit where only admins can write: Program
    // Files, or the folder the MSI installed to, which it locks down. This is the path half only;
    // the permission half is covered below with injected descriptors.
    [Theory]
    [InlineData(@"C:\Program Files\NVMe Driver Patcher\NVMeDriverPatcher.Cli.exe", true)]
    [InlineData(@"c:\program files (x86)\NVMe Driver Patcher\NVMeDriverPatcher.Cli-win-arm64.exe", true)]
    [InlineData(@"C:\Users\someone\Downloads\NVMeDriverPatcher.Cli.exe", false)]
    [InlineData(@"C:\Program FilesX\NVMeDriverPatcher.Cli.exe", false)]
    [InlineData(@"C:\Program Files\..\Users\someone\NVMeDriverPatcher.Cli.exe", false)]
    [InlineData(@"\\server\share\Program Files\NVMeDriverPatcher.Cli.exe", false)]
    [InlineData(@"C:\Program Files", false)]
    [InlineData("D:/Apps/NVMe Driver Patcher/NVMeDriverPatcher.Cli.exe", true)]
    [InlineData("D:/Apps/NVMe Driver Patcher Old/NVMeDriverPatcher.Cli.exe", false)]
    [InlineData(@"NVMe Driver Patcher\NVMeDriverPatcher.Cli.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MatchProtectedRoot_OnlyUnderProgramFilesOrTheMsiFolder(string? exe, bool expected)
    {
        // The third root stands in for the HKLM InstallLocation of an MSI installed elsewhere.
        var roots = new[] { @"C:\Program Files", @"C:\Program Files (x86)\", "D:/Apps/NVMe Driver Patcher", "" };
        Assert.Equal(expected, SchedulerService.MatchProtectedRoot(exe, roots) is not null);
    }

    // An MSI installed straight to a drive records InstallLocation D:\, and a bare root would vouch
    // for every program on that drive.
    [Theory]
    [InlineData(@"D:\")]
    [InlineData("D:/")]
    [InlineData(@"\\server\share\")]
    public void MatchProtectedRoot_IgnoresARootOnlyInstallLocation(string installLocation)
    {
        var roots = new[] { @"C:\Program Files", installLocation };

        Assert.Null(SchedulerService.MatchProtectedRoot(@"D:\Downloads\NVMeDriverPatcher.Cli.exe", roots));
        Assert.Null(SchedulerService.MatchProtectedRoot(@"\\server\share\NVMeDriverPatcher.Cli.exe", roots));
        var check = SchedulerService.CheckTaskTarget(@"D:\Downloads\NVMeDriverPatcher.Cli.exe", roots, Reader());
        Assert.False(check.IsProtected);
        Assert.Contains("isn't under Program Files", check.Reason);
    }

    private const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    // Read off a stock Windows 11 install. Program Files: TrustedInstaller owns it, SYSTEM and
    // Administrators modify, Users and app packages read, and the GA entries are inherit-only.
    private const string ProgramFilesSddl =
        "O:" + TrustedInstaller + "D:P(A;;FA;;;" + TrustedInstaller + ")(A;CIIO;GA;;;" + TrustedInstaller + ")" +
        "(A;;0x1301bf;;;SY)(A;OICIIO;GA;;;SY)(A;;0x1301bf;;;BA)(A;OICIIO;GA;;;BA)" +
        "(A;;0x1200a9;;;BU)(A;OICIIO;GXGR;;;BU)(A;OICIIO;GA;;;CO)(A;;0x1200a9;;;AC)";

    // C:\ lets Authenticated Users create folders (0x4, this folder only), and hands them Modify
    // only on what they create (inherit-only), so nobody else can rename Program Files.
    private const string SystemDriveSddl =
        "O:" + TrustedInstaller + "D:P(A;;0x4;;;AU)(A;OICIIO;0xe0010000;;;AU)" +
        "(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

    // A folder an installer made under Program Files, and the exe inside it, inheriting from it.
    private const string ProgramFilesChildSddl =
        "O:BAD:(A;ID;FA;;;SY)(A;OICIIOID;GA;;;SY)(A;ID;FA;;;BA)(A;OICIIOID;GA;;;BA)" +
        "(A;ID;0x1200a9;;;BU)(A;OICIIOID;GXGR;;;BU)(A;OICIIOID;GA;;;CO)";
    private const string ProgramFilesExeSddl = "O:SYD:(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;0x1200a9;;;BU)";

    // What the MSI pins on INSTALLFOLDER (NVMeDriverPatcher.wxs, InstallFolderSecurity).
    private const string MsiFolderSddl = "O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";

    // A fresh NTFS data volume: Authenticated Users get Modify on everything, the root included.
    private const string DataDriveSddl =
        "O:BAD:(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;0x1301bf;;;AU)(A;OICIIO;GA;;;CO)(A;OICI;0x1200a9;;;BU)";

    private static readonly string[] ProgramFilesRoots = { @"C:\Program Files", @"C:\Program Files (x86)" };

    private const string StockExe = @"C:\Program Files\NVMe Driver Patcher\NVMeDriverPatcher.Cli.exe";

    private static Dictionary<string, string> StockProgramFiles() => new(StringComparer.OrdinalIgnoreCase)
    {
        [StockExe] = ProgramFilesExeSddl,
        [@"C:\Program Files\NVMe Driver Patcher"] = ProgramFilesChildSddl,
        [@"C:\Program Files"] = ProgramFilesSddl,
        [@"C:\"] = SystemDriveSddl,
    };

    [Fact]
    public void CheckTaskTarget_AcceptsStockProgramFilesAndReadsEveryFolderToTheDrive()
    {
        var read = new List<string>();
        var check = SchedulerService.CheckTaskTarget(StockExe, ProgramFilesRoots, Reader(StockProgramFiles(), read));

        Assert.True(check.IsProtected, check.Reason);
        Assert.Equal(new[] { StockExe, @"C:\Program Files\NVMe Driver Patcher", @"C:\Program Files", @"C:\" }, read);
    }

    [Fact]
    public void CheckTaskTarget_RefusesAUserWritableFolderUnderProgramFiles()
    {
        // Another vendor's folder that lets Users write. A CLI copied in there passes the path test,
        // and anyone could swap it before the next SYSTEM run.
        var acls = StockProgramFiles();
        acls[@"C:\Program Files\NVMe Driver Patcher"] =
            "O:BAD:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;BU)";

        var check = SchedulerService.CheckTaskTarget(StockExe, ProgramFilesRoots, Reader(acls));

        Assert.False(check.IsProtected);
        Assert.EndsWith(@"can add, change or delete files in C:\Program Files\NVMe Driver Patcher.", check.Reason);
    }

    [Fact]
    public void CheckTaskTarget_RefusesAnExeOnlyItsOwnerShouldControl()
    {
        // Owned by Users: the owner can rewrite the DACL whenever it likes, whatever it says today.
        var acls = StockProgramFiles();
        acls[StockExe] = "O:BUD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)";

        var check = SchedulerService.CheckTaskTarget(StockExe, ProgramFilesRoots, Reader(acls));

        Assert.False(check.IsProtected);
        Assert.StartsWith(StockExe + " is owned by ", check.Reason);
    }

    [Fact]
    public void CheckTaskTarget_AcceptsTheMsiDaclInACustomFolder()
    {
        const string exe = @"D:\Apps\NVMe Driver Patcher\NVMeDriverPatcher.Cli.exe";
        var roots = new[] { @"C:\Program Files", @"D:\Apps\NVMe Driver Patcher" };
        var acls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [exe] = MsiFolderSddl,
            [@"D:\Apps\NVMe Driver Patcher"] = MsiFolderSddl,
            [@"D:\Apps"] = MsiFolderSddl,
            // Authenticated Users hold Modify, DELETE included, on the drive root, but a root
            // can't be renamed, so only DELETE_CHILD or re-permission rights would count there.
            [@"D:\"] = DataDriveSddl,
        };

        var check = SchedulerService.CheckTaskTarget(exe, roots, Reader(acls));

        Assert.True(check.IsProtected, check.Reason);
    }

    [Fact]
    public void CheckTaskTarget_RefusesTheMsiFolderWhenAnyoneCanRenameTheFolderAboveIt()
    {
        // The MSI locks its own folder, but D:\Apps inherited Modify for Authenticated Users from
        // the drive. Any of them can rename D:\Apps and build a fake tree at the same path.
        const string exe = @"D:\Apps\NVMe Driver Patcher\NVMeDriverPatcher.Cli.exe";
        var roots = new[] { @"D:\Apps\NVMe Driver Patcher" };
        var acls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [exe] = MsiFolderSddl,
            [@"D:\Apps\NVMe Driver Patcher"] = MsiFolderSddl,
            [@"D:\Apps"] = DataDriveSddl,
            [@"D:\"] = DataDriveSddl,
        };

        var check = SchedulerService.CheckTaskTarget(exe, roots, Reader(acls));

        Assert.False(check.IsProtected);
        Assert.Contains(@"can rename or re-permission D:\Apps,", check.Reason);
    }

    [Fact]
    public void CheckTaskTarget_IgnoresAnInheritOnlyCreatorOwnerEntry()
    {
        // CREATOR OWNER GENERIC_ALL marked inherit-only only seeds new children. It grants nothing
        // on the folder itself. Without the IO flag the same entry is a real grant and must fail,
        // which proves the first result isn't a check that ignores CREATOR OWNER altogether.
        var acls = StockProgramFiles();
        acls[@"C:\Program Files\NVMe Driver Patcher"] = MsiFolderSddl + "(A;OICIIO;GA;;;CO)";
        Assert.True(SchedulerService.CheckTaskTarget(StockExe, ProgramFilesRoots, Reader(acls)).IsProtected);

        acls[@"C:\Program Files\NVMe Driver Patcher"] = MsiFolderSddl + "(A;OICI;GA;;;CO)";
        Assert.False(SchedulerService.CheckTaskTarget(StockExe, ProgramFilesRoots, Reader(acls)).IsProtected);
    }

    [Fact]
    public void CheckTaskTarget_RefusesWhenAPermissionReadFails()
    {
        var acls = StockProgramFiles();
        var reader = Reader(acls);
        PathSecurity Failing(string path) =>
            path.Equals(@"C:\Program Files", StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException("Access is denied.")
                : reader(path);

        var check = SchedulerService.CheckTaskTarget(StockExe, ProgramFilesRoots, Failing);

        Assert.False(check.IsProtected);
        Assert.StartsWith(@"Couldn't read the permissions on C:\Program Files ", check.Reason);
    }

    [Fact]
    public void CheckTaskTarget_RefusesAJunctionAnywhereOnThePath()
    {
        var acls = StockProgramFiles();
        var reader = Reader(acls);
        PathSecurity Linked(string path) =>
            path.Equals(@"C:\Program Files\NVMe Driver Patcher", StringComparison.OrdinalIgnoreCase)
                ? reader(path) with { IsReparsePoint = true }
                : reader(path);

        var check = SchedulerService.CheckTaskTarget(StockExe, ProgramFilesRoots, Linked);

        Assert.False(check.IsProtected);
        Assert.StartsWith(@"C:\Program Files\NVMe Driver Patcher is a junction", check.Reason);
    }

    [Fact]
    public void CheckTaskTarget_RealReaderRefusesAFolderTheCurrentUserOwns()
    {
        // A fresh folder under %TEMP% belongs to whoever made it (or, when elevated, grants that
        // user full control through inheritance), so even named as a protected root it's refused.
        var dir = Path.Combine(Path.GetTempPath(), "nvme-sched-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "NVMeDriverPatcher.Cli.exe");
            File.WriteAllBytes(exe, [0x4D, 0x5A]);

            var check = SchedulerService.CheckTaskTarget(exe, new[] { dir }, SchedulerService.ReadPathSecurity);

            Assert.False(check.IsProtected);
            Assert.Contains(dir, check.Reason);
            Assert.DoesNotContain("Couldn't read", check.Reason);
            Assert.DoesNotContain("doesn't exist", check.Reason);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RegisterTasks_RefusesAnUnprotectedExeBeforeRegisteringAnything()
    {
        var program = File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "NVMeDriverPatcher.Cli", "Program.cs")));
        var body = program[program.IndexOf("static int RegisterTasksCommand(", StringComparison.Ordinal)..];
        var guard = body.IndexOf("SchedulerService.CheckTaskTarget(cliExe)", StringComparison.Ordinal);
        var register = body.IndexOf("SchedulerService.Register", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < register, "the protection check must run before any task is registered");
    }

    [Fact]
    public void QueryXmlArgs_AskForOneTaskDefinition()
    {
        Assert.Equal(new[] { "/Query", "/TN", SchedulerService.BootTaskName, "/XML" },
            SchedulerService.BuildQueryXmlArgs(SchedulerService.BootTaskName));
    }

    // schtasks /Query /XML as Windows prints it: a UTF-16 declaration on what is already a string, a
    // leading blank line, and the quotes kept around a path with spaces.
    private static string TaskXml(string command) => $"""

        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <URI>\SysAdminDoc\NVMePatcher\BootVerify</URI>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-18</UserId>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Actions Context="Author">
            <Exec>
              <Command>{command}</Command>
              <Arguments>watchdog --auto-revert</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    [Fact]
    public void ParseTaskExecCommands_ReadsTheQuotedProgramOutOfSchtasksXml()
    {
        Assert.Equal(new[] { StockExe }, SchedulerService.ParseTaskExecCommands(TaskXml("\"" + StockExe + "\"")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<Task><Actions>")]
    [InlineData("ERROR: The system cannot find the file specified.")]
    [InlineData("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Settings /></Task>")]
    [InlineData("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions><Exec><Command> \"\" </Command></Exec></Actions></Task>")]
    public void ParseTaskExecCommands_ReturnsNullForAnythingItCantRead(string? xml) =>
        Assert.Null(SchedulerService.ParseTaskExecCommands(xml));

    private static TaskTargetCheck StockCheck(string exe) =>
        SchedulerService.CheckTaskTarget(exe, ProgramFilesRoots, Reader(StockProgramFiles()));

    [Fact]
    public void EvaluateTaskQuery_ATaskRunningTheInstalledCliIsProtected()
    {
        var audit = SchedulerService.EvaluateTaskQuery(
            SchedulerService.BootTaskName, 0, TaskXml("\"" + StockExe + "\""), StockCheck);

        Assert.Equal(ScheduledTaskTargetState.Protected, audit.State);
        Assert.Equal(StockExe, audit.Target);
        Assert.False(audit.NeedsAttention);
    }

    [Fact]
    public void EvaluateTaskQuery_FlagsATaskRegisteredAgainstADownloadsCopy()
    {
        // What a task registered before register-tasks checked its target can still point at.
        const string portable = @"C:\Users\someone\Downloads\NVMeDriverPatcher.Cli.exe";

        var audit = SchedulerService.EvaluateTaskQuery(
            SchedulerService.WatchdogTaskName, 0, TaskXml(portable), StockCheck);

        Assert.Equal(ScheduledTaskTargetState.Unprotected, audit.State);
        Assert.Equal(portable, audit.Target);
        Assert.True(audit.NeedsAttention);
        Assert.Contains(portable + " isn't under Program Files", audit.Detail);
        Assert.Contains("unregister-tasks", audit.Detail);
    }

    [Fact]
    public void EvaluateTaskQuery_AMissingTaskIsNotAWarning()
    {
        var audit = SchedulerService.EvaluateTaskQuery(
            SchedulerService.BootTaskName, 1, "", _ => throw new InvalidOperationException("nothing to check"));

        Assert.Equal(ScheduledTaskTargetState.NotRegistered, audit.State);
        Assert.False(audit.NeedsAttention);
    }

    // A definition that can't be parsed, or a schtasks that never answered, must not read as safe.
    [Theory]
    [InlineData(0, "<Task><Actions>")]
    [InlineData(null, "")]
    public void EvaluateTaskQuery_MalformedXmlOrNoAnswerIsFlaggedNotPassed(int? exitCode, string xml)
    {
        var audit = SchedulerService.EvaluateTaskQuery(
            SchedulerService.WatchdogTaskName, exitCode, xml, _ => new TaskTargetCheck(true, "would pass"));

        Assert.Equal(ScheduledTaskTargetState.Unreadable, audit.State);
        Assert.True(audit.NeedsAttention);
    }

    // Every path the check asks about gets the descriptor mapped to it, or an admin-only one.
    private static Func<string, PathSecurity> Reader(
        Dictionary<string, string>? sddlByPath = null,
        List<string>? read = null) =>
        path =>
        {
            read?.Add(path);
            var sddl = sddlByPath is not null && sddlByPath.TryGetValue(path, out var mapped) ? mapped : MsiFolderSddl;
            var descriptor = new DirectorySecurity();
            descriptor.SetSecurityDescriptorSddlForm(sddl);
            return new PathSecurity(descriptor);
        };

    private static void AssertPair(string[] args, string flag, string expectedValue)
    {
        var idx = Array.IndexOf(args, flag);
        Assert.True(idx >= 0 && idx + 1 < args.Length, $"flag {flag} not found with a value");
        Assert.Equal(expectedValue, args[idx + 1]);
    }
}
