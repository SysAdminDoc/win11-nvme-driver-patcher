using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace NVMeDriverPatcher.Tests;

public sealed class DocumentationFactsValidatorTests
{
    [Fact]
    public void Validator_AcceptsFactsDerivedFromFixtureSources()
    {
        using var fixture = new DocumentationFixture();

        var result = RunValidator(fixture.Root);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("2 commands; 12 discovered tests (floor 10)", result.Output);
    }

    [Theory]
    [InlineData("command", "README.md command count '3' should be '2'")]
    [InlineData("runtime", "README.md .NET badge major '9' should be '10'")]
    [InlineData("tests", "README.md discovered-test floor '20' exceeds actual discovery count '12'")]
    [InlineData("path", "README.md repository path 'src/Missing.cs' does not exist")]
    public void Validator_NamesTheExactStaleField(string drift, string expectedFailure)
    {
        using var fixture = new DocumentationFixture();
        fixture.Introduce(drift);

        var result = RunValidator(fixture.Root);

        Assert.Equal(1, result.ExitCode);
        Assert.True(result.Output.Contains(expectedFailure, StringComparison.Ordinal), result.Output);
    }

    private static (int ExitCode, string Output) RunValidator(string root)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(ValidatorPath());
        process.StartInfo.ArgumentList.Add("-RepoRoot");
        process.StartInfo.ArgumentList.Add(root);
        process.StartInfo.ArgumentList.Add("-DiscoveredTestCount");
        process.StartInfo.ArgumentList.Add("12");

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(20_000), "Documentation validator timed out.");
        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static string ValidatorPath([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourceFile)!,
            "..", "..", "scripts", "Validate-DocumentationFacts.ps1"));

    private sealed class DocumentationFixture : IDisposable
    {
        private static readonly string[] ProjectPaths =
        {
            "src/NVMeDriverPatcher.Core/NVMeDriverPatcher.Core.csproj",
            "src/NVMeDriverPatcher/NVMeDriverPatcher.csproj",
            "src/NVMeDriverPatcher.Cli/NVMeDriverPatcher.Cli.csproj",
            "src/NVMeDriverPatcher.Tray/NVMeDriverPatcher.Tray.csproj",
            "src/NVMeDriverPatcher.Watchdog/NVMeDriverPatcher.Watchdog.csproj"
        };

        public DocumentationFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "nvmepatcher-doc-facts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Write("README.md", """
                ![Version](https://img.shields.io/badge/Version-5.0.0-blue)
                ![.NET](https://img.shields.io/badge/.NET-10.0-blue)
                ### Extended CLI (C# binary — 2 commands)
                - **Automated verification** -- 10+ discovered test cases cover safety behavior.
                `src/NVMeDriverPatcher.Core/NVMeDriverPatcher.Core.csproj`
                """);
            Write("Directory.Build.props", "<Project><PropertyGroup><VersionPrefix>5.0.0</VersionPrefix></PropertyGroup></Project>");
            Write("global.json", "{\"sdk\":{\"version\":\"10.0.301\"}}");
            Write("src/NVMeDriverPatcher.Cli/CliCommandRegistry.cs", """
                public static readonly CliCommandDescriptor[] All =
                {
                    new("status", [], CommandGroup.Lifecycle, "Status"),
                    new("apply", [], CommandGroup.Lifecycle, "Apply")
                };
                """);
            foreach (var project in ProjectPaths)
                Write(project, "<Project><PropertyGroup><TargetFramework>net10.0-windows10.0.19041.0</TargetFramework></PropertyGroup></Project>");
        }

        public string Root { get; }

        public void Introduce(string drift)
        {
            var readmePath = Path.Combine(Root, "README.md");
            var readme = File.ReadAllText(readmePath);
            readme = drift switch
            {
                "command" => readme.Replace("2 commands", "3 commands", StringComparison.Ordinal),
                "runtime" => readme.Replace(".NET-10.0", ".NET-9.0", StringComparison.Ordinal),
                "tests" => readme.Replace("10+ discovered", "20+ discovered", StringComparison.Ordinal),
                "path" => readme + Environment.NewLine + "`src/Missing.cs`",
                _ => throw new ArgumentOutOfRangeException(nameof(drift))
            };
            File.WriteAllText(readmePath, readme);
        }

        private void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
