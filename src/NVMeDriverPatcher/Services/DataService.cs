using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NVMeDriverPatcher.Data;
using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

public static class DataService
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false
    };

    public static void Initialize()
    {
        AppDbContext.EnsureCreated();
    }

    public static void SaveBenchmark(BenchmarkResult result)
    {
        if (result is null) return;
        try
        {
            using var db = new AppDbContext();

            var record = new BenchmarkRecord
            {
                Label = result.Label ?? string.Empty,
                // Persist UTC so prune windows + sort order stay stable across DST transitions
                // and timezone moves. UI renders as local time.
                Timestamp = DateTime.TryParse(result.Timestamp,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var ts)
                    ? ts.ToUniversalTime() : DateTime.UtcNow,
                ReadIOPS = result.Read?.IOPS ?? 0,
                ReadThroughputMBs = result.Read?.ThroughputMBs ?? 0,
                ReadLatencyMs = result.Read?.AvgLatencyMs ?? 0,
                WriteIOPS = result.Write?.IOPS ?? 0,
                WriteThroughputMBs = result.Write?.ThroughputMBs ?? 0,
                WriteLatencyMs = result.Write?.AvgLatencyMs ?? 0
            };

            db.Benchmarks.Add(record);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DataService] SaveBenchmark failed: {ex.Message}");
        }
    }

    public static List<BenchmarkRecord> GetBenchmarkHistory(int limit = 50)
    {
        if (limit <= 0) limit = 50;
        try
        {
            using var db = new AppDbContext();
            return db.Benchmarks
                .OrderByDescending(b => b.Timestamp)
                .Take(limit)
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DataService] GetBenchmarkHistory failed: {ex.Message}");
            return [];
        }
    }

    public static void SaveSnapshot(PatchSnapshot snapshot, string description, bool isPrePatch)
    {
        if (snapshot is null) return;
        try
        {
            using var db = new AppDbContext();

            var record = new SnapshotRecord
            {
                // Persist UTC so retention / prune windows stay correct across DST.
                Timestamp = DateTime.TryParse(snapshot.Timestamp,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var ts)
                    ? ts.ToUniversalTime() : DateTime.UtcNow,
                Description = description ?? string.Empty,
                RegistryStateJson = JsonSerializer.Serialize(snapshot.Components ?? [], _jsonOpts),
                // Include Total and Keys too — without them, replaying a snapshot from disk loses
                // the context needed to render an accurate "Applied 3/5" comparison later.
                PatchStatusJson = JsonSerializer.Serialize(new
                {
                    snapshot.Status.Applied,
                    snapshot.Status.Partial,
                    snapshot.Status.Count,
                    snapshot.Status.Total,
                    snapshot.Status.Keys,
                    snapshot.DriverActive,
                    snapshot.BypassIO
                }, _jsonOpts),
                IsPrePatch = isPrePatch
            };

            db.Snapshots.Add(record);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DataService] SaveSnapshot failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the most recent snapshots (newest first). Capped to keep the workspace
    /// summary cheap even after months of patch activity.
    /// </summary>
    public static List<SnapshotRecord> GetSnapshots(int limit = 100)
    {
        if (limit <= 0) limit = 100;
        try
        {
            using var db = new AppDbContext();
            return db.Snapshots
                .OrderByDescending(s => s.Timestamp)
                .Take(limit)
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DataService] GetSnapshots failed: {ex.Message}");
            return [];
        }
    }

    public static void SaveTelemetry(
        int driveNumber,
        int temperatureCelsius,
        int availableSparePercent,
        int percentageUsed,
        long dataUnitsRead,
        long dataUnitsWritten,
        long powerOnHours,
        int mediaErrors,
        int unsafeShutdowns)
    {
        if (driveNumber < 0) return;
        try
        {
            using var db = new AppDbContext();

            var record = new TelemetryRecord
            {
                DriveNumber = driveNumber,
                Timestamp = DateTime.UtcNow,
                TemperatureCelsius = temperatureCelsius,
                AvailableSparePercent = availableSparePercent,
                PercentageUsed = percentageUsed,
                DataUnitsRead = dataUnitsRead,
                DataUnitsWritten = dataUnitsWritten,
                PowerOnHours = powerOnHours,
                MediaErrors = mediaErrors,
                UnsafeShutdowns = unsafeShutdowns
            };

            db.Telemetry.Add(record);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DataService] SaveTelemetry failed: {ex.Message}");
        }
    }

    public static List<TelemetryRecord> GetTelemetryHistory(int driveNumber, TimeSpan window)
    {
        if (driveNumber < 0) return [];
        if (window <= TimeSpan.Zero) window = TimeSpan.FromDays(7);
        try
        {
            using var db = new AppDbContext();
            // UTC across the board so queries over the Timestamp column stay stable against
            // DST transitions — otherwise rows written at 02:00 on a DST spring-forward day
            // could fall outside or inside the window depending on when the query ran.
            var cutoff = DateTime.UtcNow - window;
            return db.Telemetry
                .Where(t => t.DriveNumber == driveNumber && t.Timestamp >= cutoff)
                .OrderBy(t => t.Timestamp)
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DataService] GetTelemetryHistory failed: {ex.Message}");
            return [];
        }
    }

    public static TelemetryRecord? GetLatestTelemetry(int driveNumber)
    {
        if (driveNumber < 0) return null;
        try
        {
            using var db = new AppDbContext();
            return db.Telemetry
                .Where(t => t.DriveNumber == driveNumber)
                .OrderByDescending(t => t.Timestamp)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DataService] GetLatestTelemetry failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Removes telemetry rows older than the retention window. Default is 90 days.
    /// Without periodic pruning the DB grows forever as the user runs the app over months.
    /// Uses ExecuteDelete (single SQL DELETE) instead of materializing every stale row into
    /// memory first — important after years of telemetry accumulates.
    /// </summary>
    public static int PruneTelemetry(TimeSpan? retention = null)
    {
        try
        {
            using var db = new AppDbContext();
            var window = retention ?? TimeSpan.FromDays(90);
            var cutoff = DateTime.UtcNow - window;
            return db.Telemetry.Where(t => t.Timestamp < cutoff).ExecuteDelete();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DataService] PruneTelemetry failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Removes snapshot rows beyond a generous retention cap. The workspace summary already
    /// limits queries to the newest 100, so older rows are pure storage cost.
    /// </summary>
    public static int PruneSnapshots(int keepNewest = 500)
    {
        if (keepNewest < 100) keepNewest = 100;
        try
        {
            using var db = new AppDbContext();
            // Delete by primary key so identical timestamps don't over-delete the boundary row.
            var idsToDelete = db.Snapshots
                .OrderByDescending(s => s.Timestamp)
                .ThenByDescending(s => s.Id)
                .Skip(keepNewest)
                .Select(s => s.Id)
                .ToList();
            if (idsToDelete.Count == 0) return 0;
            return db.Snapshots.Where(s => idsToDelete.Contains(s.Id)).ExecuteDelete();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DataService] PruneSnapshots failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Removes benchmark rows beyond a generous retention cap. The JSON history file already
    /// keeps only the newest ten results, and the UI only renders a small recent slice.
    /// </summary>
    public static int PruneBenchmarks(int keepNewest = 500)
    {
        if (keepNewest < 50) keepNewest = 50;
        try
        {
            using var db = new AppDbContext();
            var idsToDelete = db.Benchmarks
                .OrderByDescending(b => b.Timestamp)
                .ThenByDescending(b => b.Id)
                .Skip(keepNewest)
                .Select(b => b.Id)
                .ToList();
            if (idsToDelete.Count == 0) return 0;
            return db.Benchmarks.Where(b => idsToDelete.Contains(b.Id)).ExecuteDelete();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DataService] PruneBenchmarks failed: {ex.Message}");
            return 0;
        }
    }
}
