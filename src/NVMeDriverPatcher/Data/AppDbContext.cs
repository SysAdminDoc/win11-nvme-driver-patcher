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

        // Cache=Private is the safe default for a single-process WPF app. Shared cache + WAL
        // can produce surprising lock-conflict behaviour with EF Core's per-query connections.
        // DefaultTimeout bumped to 10s so a transient lock during telemetry pruning doesn't
        // bubble up as an exception during Save.
        var connStr = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Private,
            DefaultTimeout = 10
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
        // WAL improves concurrency between read pollers and writes; busy_timeout backs the
        // SQLite engine's own retry loop in case a brief writer blocks a reader.
        try { db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;"); } catch { }
        try { db.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;"); } catch { }
        try { db.Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;"); } catch { }
    }
}
