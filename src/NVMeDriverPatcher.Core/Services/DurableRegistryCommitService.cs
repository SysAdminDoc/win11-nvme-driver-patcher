using Microsoft.Win32;

namespace NVMeDriverPatcher.Services;

/// <summary>A single critical HKLM64 value that must survive flush and fresh-handle readback.</summary>
internal sealed record DurableRegistryMutation(
    string Path,
    string ValueName,
    object ExpectedValue,
    RegistryValueKind ValueKind,
    string Label,
    bool CountsTowardPatchTotal);

internal sealed record DurableRegistryCommitResult(
    bool Success,
    string Stage,
    string Summary)
{
    public static DurableRegistryCommitResult Passed(DurableRegistryMutation mutation) =>
        new(true, "verified", $"{mutation.Path}\\{DisplayValueName(mutation.ValueName)} was flushed and reopened successfully.");

    public static DurableRegistryCommitResult Failed(
        DurableRegistryMutation mutation,
        string stage,
        string detail) =>
        new(false, stage,
            $"{mutation.Path}\\{DisplayValueName(mutation.ValueName)} failed during {stage}: {detail}");

    private static string DisplayValueName(string valueName) =>
        string.IsNullOrEmpty(valueName) ? "(Default)" : valueName;
}

internal sealed record DurableRegistryBatchResult(
    bool Success,
    int CountedCommitted,
    string Summary);

/// <summary>Fault-injection seam for the write, flush, and fresh-read phases.</summary>
internal interface IDurableRegistryPlatform
{
    void Write(DurableRegistryMutation mutation);
    void Flush(string path);
    object? ReadFresh(string path, string valueName);
}

/// <summary>
/// Commits one registry value only after SetValue, Flush, and a newly-opened read handle all
/// succeed. A same-handle GetValue is not durability evidence because it may observe cached state.
/// </summary>
internal static class DurableRegistryCommitService
{
    public static DurableRegistryBatchResult CommitAll(
        IReadOnlyList<DurableRegistryMutation> mutations,
        IDurableRegistryPlatform platform,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(platform);

        var countedCommitted = 0;
        foreach (var mutation in mutations)
        {
            var commit = Commit(mutation, platform);
            if (!commit.Success)
            {
                log?.Invoke($"  [FAIL] {mutation.Label}: {commit.Summary}");
                return new DurableRegistryBatchResult(false, countedCommitted, commit.Summary);
            }

            log?.Invoke($"  [OK] {mutation.Label} (flushed + reopened)");
            if (mutation.CountsTowardPatchTotal)
                countedCommitted++;
        }

        return new DurableRegistryBatchResult(true, countedCommitted,
            $"All {mutations.Count} critical registry values were flushed and reopened successfully.");
    }

    public static DurableRegistryCommitResult Commit(
        DurableRegistryMutation mutation,
        IDurableRegistryPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(platform);

        try
        {
            platform.Write(mutation);
        }
        catch (Exception ex)
        {
            return DurableRegistryCommitResult.Failed(mutation, "write", Describe(ex));
        }

        try
        {
            platform.Flush(mutation.Path);
        }
        catch (Exception ex)
        {
            return DurableRegistryCommitResult.Failed(mutation, "flush", Describe(ex));
        }

        object? actual;
        try
        {
            actual = platform.ReadFresh(mutation.Path, mutation.ValueName);
        }
        catch (Exception ex)
        {
            return DurableRegistryCommitResult.Failed(mutation, "reopen/readback", Describe(ex));
        }

        if (!ValuesEqual(mutation.ExpectedValue, actual))
        {
            return DurableRegistryCommitResult.Failed(
                mutation,
                "reopen/readback",
                $"expected {Format(mutation.ExpectedValue)}, observed {Format(actual)}");
        }

        return DurableRegistryCommitResult.Passed(mutation);
    }

    private static bool ValuesEqual(object expected, object? actual) =>
        expected switch
        {
            int expectedInt => actual is int actualInt && actualInt == expectedInt,
            string expectedString => actual is string actualString &&
                                     actualString.Equals(expectedString, StringComparison.Ordinal),
            _ => Equals(expected, actual)
        };

    private static string Format(object? value) => value switch
    {
        null => "<missing>",
        string text => $"'{text}'",
        _ => value.ToString() ?? "<unprintable>"
    };

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";
}

internal sealed class WindowsDurableRegistryPlatform : IDurableRegistryPlatform
{
    public void Write(DurableRegistryMutation mutation)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.CreateSubKey(mutation.Path, writable: true) ??
                        throw new IOException("CreateSubKey returned null.");
        key.SetValue(mutation.ValueName, mutation.ExpectedValue, mutation.ValueKind);
    }

    public void Flush(string path)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(path, writable: true) ??
                        throw new IOException("The key disappeared before flush.");
        key.Flush();
    }

    public object? ReadFresh(string path, string valueName)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(path, writable: false) ??
                        throw new IOException("The key could not be reopened after flush.");
        return key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }
}
