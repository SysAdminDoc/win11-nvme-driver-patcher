using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public enum ConfigLoadStatus
{
    LoadedPrimary,
    RecoveredFromBackup,
    Defaults,
    Busy,
    Failed
}

public sealed record ConfigLoadResult(AppConfig Config, ConfigLoadStatus Status, string Summary)
{
    public bool Success => Status is ConfigLoadStatus.LoadedPrimary or
        ConfigLoadStatus.RecoveredFromBackup or ConfigLoadStatus.Defaults;
}

public enum ConfigSaveStatus
{
    Saved,
    Busy,
    Invalid,
    ValidationFailed,
    IoFailure
}

public sealed record ConfigSaveResult(ConfigSaveStatus Status, string Summary, string? BackupPath = null)
{
    public bool Success => Status == ConfigSaveStatus.Saved;
}

public sealed class ConfigStateBusyException(string message) : IOException(message);

public static partial class ConfigService
{
    internal const string ConfigMutexName = @"Global\NVMeDriverPatcher.ConfigSave";
    private static readonly TimeSpan ConfigMutexTimeout = TimeSpan.FromSeconds(10);

    public static AppConfig Load()
    {
        var result = LoadWithStatus();
        if (result.Status == ConfigLoadStatus.Busy)
            throw new ConfigStateBusyException(result.Summary);
        if (!result.Success)
            throw new IOException(result.Summary);
        return result.Config;
    }

    public static ConfigLoadResult LoadWithStatus() => LoadDetailed();

    internal static ConfigLoadResult LoadDetailed(
        string? workingDirOverride = null,
        TimeSpan? lockTimeout = null,
        string? mutexName = null)
    {
        var workingDir = workingDirOverride ?? AppConfig.GetWorkingDir();
        var config = new AppConfig
        {
            WorkingDir = workingDir,
            ConfigFile = Path.Combine(workingDir, "config.json")
        };

        using var lease = AcquireConfigMutex(lockTimeout ?? ConfigMutexTimeout, mutexName ?? ConfigMutexName);
        if (!lease.Held)
        {
            return new ConfigLoadResult(
                config,
                lease.Error is null ? ConfigLoadStatus.Busy : ConfigLoadStatus.Failed,
                lease.Error ?? "Configuration is busy in another process; no protected state was read. Retry after the current operation finishes.");
        }

        if (workingDirOverride is null && AppConfig.IsSharedProgramDataWorkingDir(workingDir))
            MigrateLegacyWorkingDirIfNeeded(workingDir, AppConfig.GetLegacyUserWorkingDirPath());

        var primaryPath = config.ConfigFile;
        var backupPath = primaryPath + ".bak";
        if (File.Exists(primaryPath) && TryReadSavedConfig(primaryPath, out var primary, out _))
        {
            ApplySavedConfig(config, primary!);
            return new ConfigLoadResult(config, ConfigLoadStatus.LoadedPrimary, "Loaded validated primary config.json.");
        }

        string? primaryProblem = null;
        var primaryCanBeRestored = !File.Exists(primaryPath);
        if (File.Exists(primaryPath))
        {
            primaryProblem = "Primary config.json failed validation.";
            var corruptPath = PreserveCorruptFile(primaryPath);
            primaryCanBeRestored = corruptPath is not null;
            primaryProblem += corruptPath is null
                ? " The corrupt primary could not be preserved and was left in place."
                : $" Preserved as {Path.GetFileName(corruptPath)}.";
        }

        if (File.Exists(backupPath) && TryReadSavedConfig(backupPath, out var backup, out _))
        {
            ApplySavedConfig(config, backup!);
            var restoreError = "invalid primary evidence could not be moved";
            var restored = primaryCanBeRestored && TryRestorePrimaryFromBackup(backupPath, primaryPath, out restoreError);
            var summary = string.Join(" ", new[]
            {
                primaryProblem,
                "Loaded validated last-known-good config.json.bak.",
                restored ? "Restored the primary atomically." : $"Primary self-heal was not completed: {restoreError}"
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return new ConfigLoadResult(config, ConfigLoadStatus.RecoveredFromBackup, summary);
        }

        string? backupProblem = null;
        if (File.Exists(backupPath))
        {
            TryReadSavedConfig(backupPath, out _, out var invalidBackupError);
            var corruptBackupPath = PreserveCorruptFile(backupPath);
            backupProblem = corruptBackupPath is null
                ? $"Backup failed validation ({invalidBackupError}) and could not be preserved."
                : $"Backup failed validation ({invalidBackupError}) and was preserved as {Path.GetFileName(corruptBackupPath)}.";
        }

        var defaultsSummary = string.Join(" ", new[]
        {
            primaryProblem,
            backupProblem,
            "No validated persisted configuration is available; using safe defaults."
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new ConfigLoadResult(config, ConfigLoadStatus.Defaults, defaultsSummary);
    }

    /// <summary>Compatibility wrapper for existing callers. Safety-critical callers already
    /// treat false as an undurable checkpoint; richer callers can use <see cref="SaveWithStatus"/>.</summary>
    public static bool Save(AppConfig config) => SaveWithStatus(config).Success;

    public static ConfigSaveResult SaveWithStatus(AppConfig config) => SaveDetailed(config);

    internal static ConfigSaveResult SaveDetailed(
        AppConfig config,
        TimeSpan? lockTimeout = null,
        string? mutexName = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.ConfigFile) || !Path.IsPathFullyQualified(config.ConfigFile))
            return new ConfigSaveResult(ConfigSaveStatus.Invalid, "ConfigFile must be a fully qualified path.");

        using var lease = AcquireConfigMutex(lockTimeout ?? ConfigMutexTimeout, mutexName ?? ConfigMutexName);
        if (!lease.Held)
        {
            return new ConfigSaveResult(
                lease.Error is null ? ConfigSaveStatus.Busy : ConfigSaveStatus.IoFailure,
                lease.Error ?? "Configuration is busy in another process; no protected state was written. Retry after the current operation finishes.");
        }

        var primaryPath = config.ConfigFile;
        var backupPath = primaryPath + ".bak";
        var tempPath = $"{primaryPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        Exception? lastException = null;
        try
        {
            var directory = Path.GetDirectoryName(primaryPath);
            if (string.IsNullOrWhiteSpace(directory))
                return new ConfigSaveResult(ConfigSaveStatus.Invalid, "ConfigFile has no parent directory.");
            Directory.CreateDirectory(directory);
            var json = SerializeConfig(config);

            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    WriteDurableText(tempPath, json);
                    if (!TryReadSavedConfig(tempPath, out _, out var validationError))
                    {
                        TryDelete(tempPath);
                        return new ConfigSaveResult(
                            ConfigSaveStatus.ValidationFailed,
                            $"Flushed config staging file failed validation: {validationError}");
                    }

                    PromoteValidatedConfig(tempPath, primaryPath, backupPath);
                    if (!TryReadSavedConfig(primaryPath, out _, out var primaryError))
                        throw new InvalidDataException($"Promoted primary failed validation: {primaryError}");
                    if (!TryReadSavedConfig(backupPath, out _, out var backupError))
                        throw new InvalidDataException($"Retained backup failed validation: {backupError}");

                    return new ConfigSaveResult(
                        ConfigSaveStatus.Saved,
                        "Validated config saved atomically with a durable last-known-good backup.",
                        backupPath);
                }
                catch (InvalidDataException ex)
                {
                    return new ConfigSaveResult(
                        ConfigSaveStatus.ValidationFailed,
                        "Config publication failed post-write validation; the retained backup was not replaced again: " + ex.Message,
                        backupPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lastException = ex;
                    if (attempt < 5) Thread.Sleep(100);
                }
            }
        }
        catch (Exception ex)
        {
            lastException = ex;
        }
        finally
        {
            TryDelete(tempPath);
        }

        var summary = $"Failed to save config.json: {lastException?.GetType().Name}: {lastException?.Message}";
        EventLogService.Write(summary, EventLogEntryType.Warning, 3010);
        return new ConfigSaveResult(ConfigSaveStatus.IoFailure, summary, backupPath);
    }

    private static string SerializeConfig(AppConfig config)
    {
        var persisted = new
        {
            config.AutoSaveLog,
            config.EnableToasts,
            config.WriteEventLog,
            config.RestartDelay,
            config.IncludeServerKey,
            config.SkipWarnings,
            config.ThemeMode,
            config.PatchProfile,
            config.ConfigVersion,
            config.LastRun,
            config.LastRecoveryKitPath,
            config.LastDiagnosticsPath,
            config.LastSupportBundlePath,
            config.LastVerificationScriptPath,
            config.PendingVerificationSince,
            config.PendingVerificationProfile,
            config.PendingFallbackApplied,
            config.LastVerifiedProfile,
            config.LastVerificationResult
        };
        return JsonSerializer.Serialize(persisted, JsonOptions);
    }

    internal static bool TryReadSavedConfig(string path, out AppConfig? saved, out string error)
    {
        saved = null;
        error = string.Empty;
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "file is empty";
                return false;
            }
            saved = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (saved is null)
            {
                error = "JSON deserialized to null";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static void ApplySavedConfig(AppConfig config, AppConfig saved)
    {
        config.AutoSaveLog = saved.AutoSaveLog;
        config.EnableToasts = saved.EnableToasts;
        config.WriteEventLog = saved.WriteEventLog;
        config.RestartDelay = saved.RestartDelay;
        config.IncludeServerKey = saved.IncludeServerKey;
        config.SkipWarnings = saved.SkipWarnings;
        config.ThemeMode = Enum.IsDefined(typeof(AppThemeMode), saved.ThemeMode)
            ? saved.ThemeMode
            : AppThemeMode.System;
        config.PatchProfile = Enum.IsDefined(typeof(PatchProfile), saved.PatchProfile)
            ? saved.PatchProfile
            : PatchProfile.Safe;
        config.ConfigVersion = saved.ConfigVersion == 0
            ? ConfigMigrationService.CurrentSchemaVersion
            : saved.ConfigVersion;
        config.LastRun = saved.LastRun;
        config.LastRecoveryKitPath = ExistingDir(saved.LastRecoveryKitPath);
        config.LastDiagnosticsPath = ExistingFileWithExtension(saved.LastDiagnosticsPath, ".txt");
        config.LastSupportBundlePath = ExistingFileWithExtension(saved.LastSupportBundlePath, ".zip");
        config.LastVerificationScriptPath = ExistingFile(saved.LastVerificationScriptPath);
        config.PendingVerificationSince = saved.PendingVerificationSince;
        config.PendingVerificationProfile = saved.PendingVerificationProfile;
        config.PendingFallbackApplied = saved.PendingFallbackApplied;
        config.LastVerifiedProfile = saved.LastVerifiedProfile;
        config.LastVerificationResult = saved.LastVerificationResult;
    }

    private static void PromoteValidatedConfig(string tempPath, string primaryPath, string backupPath)
    {
        if (File.Exists(primaryPath))
        {
            if (TryReadSavedConfig(primaryPath, out _, out _))
            {
                File.Replace(tempPath, primaryPath, backupPath, ignoreMetadataErrors: true);
                return;
            }

            var corruptPath = PreserveCorruptFile(primaryPath);
            if (corruptPath is null)
                throw new IOException("The invalid primary could not be preserved; refusing to overwrite its evidence.");
        }

        File.Move(tempPath, primaryPath, overwrite: false);
        EnsureValidBackup(primaryPath, backupPath);
    }

    private static void EnsureValidBackup(string primaryPath, string backupPath)
    {
        if (File.Exists(backupPath) && TryReadSavedConfig(backupPath, out _, out _))
            return;
        if (File.Exists(backupPath) && PreserveCorruptFile(backupPath) is null)
            throw new IOException("The invalid config backup could not be preserved.");

        var backupTemp = $"{backupPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(primaryPath, backupTemp, overwrite: false);
            FlushExistingFile(backupTemp);
            if (!TryReadSavedConfig(backupTemp, out _, out var error))
                throw new InvalidDataException($"Backup staging validation failed: {error}");
            File.Move(backupTemp, backupPath, overwrite: false);
        }
        finally
        {
            TryDelete(backupTemp);
        }
    }

    private static bool TryRestorePrimaryFromBackup(string backupPath, string primaryPath, out string error)
    {
        error = string.Empty;
        if (File.Exists(primaryPath))
        {
            error = "invalid primary is still present, so its evidence was not overwritten";
            return false;
        }

        var tempPath = $"{primaryPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.recover.tmp";
        try
        {
            File.Copy(backupPath, tempPath, overwrite: false);
            FlushExistingFile(tempPath);
            if (!TryReadSavedConfig(tempPath, out _, out error)) return false;
            File.Move(tempPath, primaryPath, overwrite: false);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static string? PreserveCorruptFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var basePath = path + ".corrupt";
            var target = File.Exists(basePath)
                ? basePath + "." + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture)
                : basePath;
            File.Move(path, target, overwrite: false);
            return target;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteDurableText(string path, string content)
    {
        using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void FlushExistingFile(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 16 * 1024, FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static ConfigMutexLease AcquireConfigMutex(TimeSpan timeout, string mutexName)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName);
            var held = false;
            try { held = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { held = true; }
            return new ConfigMutexLease(mutex, held, null);
        }
        catch (Exception ex)
        {
            mutex?.Dispose();
            return new ConfigMutexLease(null, false, $"Configuration lock is unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed class ConfigMutexLease(Mutex? mutex, bool held, string? error) : IDisposable
    {
        public bool Held { get; } = held;
        public string? Error { get; } = error;

        public void Dispose()
        {
            if (Held) { try { mutex?.ReleaseMutex(); } catch { } }
            mutex?.Dispose();
        }
    }
}
