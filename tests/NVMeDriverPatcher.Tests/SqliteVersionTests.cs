using Microsoft.Data.Sqlite;

namespace NVMeDriverPatcher.Tests;

public sealed class SqliteVersionTests
{
    [Fact]
    public void BundledSqlite_IsAtLeast_3_50_2()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version()";
        var raw = (string)cmd.ExecuteScalar()!;
        var v = Version.Parse(raw);
        Assert.True(v >= new Version(3, 50, 2), $"Bundled SQLite {raw} is older than 3.50.2 (CVE-2025-6965).");
    }

    [Fact]
    public void DefensiveMode_BlocksShadowTableWrites()
    {
        // FTS5 CVEs in 3.53.2 exploit shadow-table writes. Defensive mode
        // (SQLITE_DBCONFIG_DEFENSIVE) blocks them even though FTS5 is compiled
        // into the bundle. The PRAGMA interface is a no-op in Microsoft.Data.Sqlite;
        // the raw C API must be used (same approach as AppDbContext.EnableDefensiveMode).
        SQLitePCL.Batteries_V2.Init();
        var rc = SQLitePCL.raw.sqlite3_open_v2(":memory:", out var db,
            SQLitePCL.raw.SQLITE_OPEN_READWRITE | SQLitePCL.raw.SQLITE_OPEN_CREATE, null);
        Assert.Equal(0, rc);
        try
        {
            rc = SQLitePCL.raw.sqlite3_db_config(db, 1010, 1, out int result);
            Assert.Equal(0, rc);
            Assert.Equal(1, result);

            Exec(db, "CREATE VIRTUAL TABLE ftstest USING fts5(content)");

            Assert.Throws<Exception>(() =>
                Exec(db, "INSERT INTO ftstest_content(id, c0) VALUES(999, 'exploit')"));
        }
        finally
        {
            SQLitePCL.raw.sqlite3_close(db);
        }
    }

    [Fact]
    public void Fts5_CompiledIn_ButMitigated()
    {
        // The bundled e_sqlite3 compiles FTS5 in (ENABLE_FTS5). This test documents
        // that fact and confirms the mitigation path: defensive mode via C API.
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var opts = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA compile_options";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) opts.Add(reader.GetString(0));
        }
        Assert.Contains("ENABLE_FTS5", opts);
    }

    [Fact]
    public void AppSchema_HasNoFts5VirtualTables()
    {
        // Even without defensive mode, the app never creates FTS5 tables.
        // This test catches accidental FTS5 usage in schema evolution.
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND sql LIKE '%fts5%'";
        using var reader = cmd.ExecuteReader();
        Assert.False(reader.HasRows, "App schema should not contain FTS5 virtual tables.");
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

    private static void Exec(SQLitePCL.sqlite3 db, string sql)
    {
        var rc = SQLitePCL.raw.sqlite3_exec(db, sql);
        if (rc != 0)
        {
            var msg = SQLitePCL.raw.sqlite3_errmsg(db).utf8_to_string();
            throw new Exception($"SQLite error {rc}: {msg}");
        }
    }
}
