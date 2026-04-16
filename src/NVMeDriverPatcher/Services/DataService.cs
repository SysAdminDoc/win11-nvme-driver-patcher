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
        try
        {
            using var db = new AppDbContext();

            var record = new BenchmarkRecord
            {
                Label = result.Label,
                Timestamp = DateTime.TryParse(result.Timestamp, out var ts) ? ts : DateTime.Now,
                ReadIOPS = result.Read.IOPS,
                ReadThroughputMBs = result.Read.ThroughputMBs,
                ReadLatencyMs = result.Read.AvgLatencyMs,
                WriteIOPS = result.Write.IOPS,
                WriteThroughputMBs = result.Write.ThroughputMBs,
                WriteLatencyMs = result.Write.AvgLatencyMs
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
        using var db = new AppDbContext();

        return db.Benchmarks
            .OrderByDescending(b => b.Timestamp)
            .Take(limit)
            .ToList();
    }

    public static void SaveSnapshot(PatchSnapshot snapshot, string description, bool isPrePatch)
    {
        try
        {
            using var db = new AppDbContext();

            var record = new SnapshotRecord
            {
                Timestamp = DateTime.TryParse(snapshot.Timestamp, out var ts) ? ts : DateTime.Now,
                Description = description,
                RegistryStateJson = JsonSerializer.Serialize(snapshot.Components, _jsonOpts),
                PatchStatusJson = JsonSerializer.Serialize(new
                {
                    snapshot.Status.Applied,
                    snapshot.Status.Partial,
                    snapshot.Status.Count
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

    public static List<SnapshotRecord> GetSnapshots()
    {
        using var db = new AppDbContext();

        return db.Snapshots
            .OrderByDescending(s => s.Timestamp)
            .ToList();
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
        try
        {
            using var db = new AppDbContext();

            var record = new TelemetryRecord
            {
                DriveNumber = driveNumber,
                Timestamp = DateTime.Now,
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
        using var db = new AppDbContext();

        var cutoff = DateTime.Now - window;

        return db.Telemetry
            .Where(t => t.DriveNumber == driveNumber && t.Timestamp >= cutoff)
            .OrderBy(t => t.Timestamp)
            .ToList();
    }

    public static TelemetryRecord? GetLatestTelemetry(int driveNumber)
    {
        using var db = new AppDbContext();

        return db.Telemetry
            .Where(t => t.DriveNumber == driveNumber)
            .OrderByDescending(t => t.Timestamp)
            .FirstOrDefault();
    }
}
