using Microsoft.EntityFrameworkCore;

namespace NVMeDriverPatcher.Data;

public class AppDbContext : DbContext
{
    public DbSet<BenchmarkRecord> Benchmarks => Set<BenchmarkRecord>();
    public DbSet<SnapshotRecord> Snapshots => Set<SnapshotRecord>();
    public DbSet<TelemetryRecord> Telemetry => Set<TelemetryRecord>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var dbPath = System.IO.Path.Combine(
            Models.AppConfig.GetWorkingDir(),
            "nvmepatcher.db");

        var connStr = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared,
            DefaultTimeout = 5
        }.ToString();

        optionsBuilder.UseSqlite(connStr);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BenchmarkRecord>(e =>
        {
            e.HasIndex(b => b.Timestamp);
        });

        modelBuilder.Entity<SnapshotRecord>(e =>
        {
            e.HasIndex(s => s.Timestamp);
        });

        modelBuilder.Entity<TelemetryRecord>(e =>
        {
            e.HasIndex(t => t.Timestamp);
            e.HasIndex(t => new { t.DriveNumber, t.Timestamp });
        });
    }

    public static void EnsureCreated()
    {
        using var db = new AppDbContext();
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }
}
