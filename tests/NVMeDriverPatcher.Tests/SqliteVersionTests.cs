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
}
