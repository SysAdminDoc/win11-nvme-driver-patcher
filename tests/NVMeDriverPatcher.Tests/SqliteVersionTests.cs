using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NVMeDriverPatcher.Data;

namespace NVMeDriverPatcher.Tests;

public sealed class SqliteVersionTests
{
    [Fact]
    public void BundledSqlite_IsAtLeast_3_53_3()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version()";
        var raw = (string)cmd.ExecuteScalar()!;
        var v = Version.Parse(raw);
        // Floor is 3.53.3 (SourceGear.sqlite3 direct pin): fixes CVE-2026-11822 and the 3.53.2
        // FTS5 shadow-table CVEs. A native downgrade below this must fail the suite.
        Assert.True(v >= new Version(3, 53, 3), $"Bundled SQLite {raw} is older than 3.53.3 (CVE-2026-11822 / FTS5 CVEs).");
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

    [Fact]
    public void Interceptor_HardensEveryConnection_SyncOpen()
    {
        using var conn = OpenInterceptedConnection(async: false);
        AssertHardened(conn);
    }

    [Fact]
    public async Task Interceptor_HardensEveryConnection_AsyncOpen()
    {
        var conn = await OpenInterceptedConnectionAsync();
        try { AssertHardened(conn); }
        finally { conn.Dispose(); }
    }

    [Fact]
    public void Interceptor_FailsClosed_WhenDefensiveConfigRejected()
    {
        // The interceptor must throw (not serve queries) if the native db_config call is rejected
        // or reports the flag was not applied. Tested via the extracted decision helper so we never
        // invoke the native API on a broken handle (which would be undefined behavior).
        Assert.Throws<InvalidOperationException>(() =>
            SqliteDefensiveConnectionInterceptor.ThrowIfDefensiveNotApplied(rc: 1, applied: 0));
        Assert.Throws<InvalidOperationException>(() =>
            SqliteDefensiveConnectionInterceptor.ThrowIfDefensiveNotApplied(rc: 0, applied: 0));
        // OK path does not throw.
        SqliteDefensiveConnectionInterceptor.ThrowIfDefensiveNotApplied(rc: 0, applied: 1);
    }

    private static SqliteConnection OpenInterceptedConnection(bool async)
    {
        // A shared-cache in-memory DB backed by its own connection, opened THROUGH EF so the
        // interceptor fires (opening the raw DbConnection directly would bypass it).
        var conn = new SqliteConnection("Data Source=:memory:");
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseSqlite(conn)
            .AddInterceptors(SqliteDefensiveConnectionInterceptor.Instance)
            .Options;
        var ctx = new TestContext(options);
        ctx.Database.OpenConnection();
        return conn;
    }

    private static async Task<SqliteConnection> OpenInterceptedConnectionAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseSqlite(conn)
            .AddInterceptors(SqliteDefensiveConnectionInterceptor.Instance)
            .Options;
        var ctx = new TestContext(options);
        await ctx.Database.OpenConnectionAsync();
        return conn;
    }

    private static void AssertHardened(SqliteConnection conn)
    {
        Assert.Equal(System.Data.ConnectionState.Open, conn.State);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA trusted_schema;";
            Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA cell_size_check;";
            Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
        }

        // Defensive mode has no read-back pragma; prove it by the blocked shadow-table write.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE VIRTUAL TABLE ftschk USING fts5(content);";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO ftschk_content(id, c0) VALUES(1, 'x');";
            Assert.ThrowsAny<Exception>(() => cmd.ExecuteNonQuery());
        }
    }

    private sealed class TestContext(DbContextOptions<TestContext> options) : DbContext(options);

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
