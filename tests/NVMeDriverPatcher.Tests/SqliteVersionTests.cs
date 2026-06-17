using Microsoft.Data.Sqlite;

namespace NVMeDriverPatcher.Tests;

public sealed class SqliteVersionTests
{
    [Fact]
    public void BundledSqlite_IsAtLeast_3_50_2()
    {
        // CVE-2025-6965 (memory corruption) is fixed in SQLite 3.50.2. The EF Core Sqlite
        // package pins an SQLitePCLRaw bundle — this test fails if a future downgrade or
        // pin regression reintroduces a vulnerable native SQLite.
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version()";
        var raw = (string)cmd.ExecuteScalar()!;
        var v = Version.Parse(raw);
        Assert.True(v >= new Version(3, 50, 2), $"Bundled SQLite {raw} is older than 3.50.2 (CVE-2025-6965).");
    }

    [Fact]
    public void HardeningPragmas_TrustedSchemaOff()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA trusted_schema=OFF;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "PRAGMA trusted_schema;";
        var val = Convert.ToInt64(cmd.ExecuteScalar());
        Assert.Equal(0, val);
    }

    [Fact]
    public void HardeningPragmas_CellSizeCheckOn()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA cell_size_check=ON;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "PRAGMA cell_size_check;";
        var val = Convert.ToInt64(cmd.ExecuteScalar());
        Assert.Equal(1, val);
    }

    [Fact]
    public void HardeningPragmas_QuickCheckReturnsOk()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check;";
        var result = (string)cmd.ExecuteScalar()!;
        Assert.Equal("ok", result);
    }
}
