using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NVMeDriverPatcher.Data;

/// <summary>
/// Applies SQLite hardening to EVERY EF connection the app opens, not just the one used by
/// <see cref="AppDbContext.EnsureCreated"/>. EF Core opens a fresh connection per query/context,
/// so <c>DataService</c> and pollers would otherwise run without defensive mode. This interceptor
/// runs on both the synchronous and asynchronous open paths and fails closed: if the defensive
/// <c>sqlite3_db_config</c> call is rejected by the native library, the open throws rather than
/// silently continuing on an unhardened connection.
/// </summary>
public sealed class SqliteDefensiveConnectionInterceptor : DbConnectionInterceptor
{
    // SQLITE_DBCONFIG_DEFENSIVE — blocks writes to shadow tables (FTS5 CVE surface) and other
    // internal structures. PRAGMA form is a no-op in Microsoft.Data.Sqlite, so it must go through
    // the raw C API on the open handle.
    private const int SQLITE_DBCONFIG_DEFENSIVE = 1010;

    public static readonly SqliteDefensiveConnectionInterceptor Instance = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Harden(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Harden(connection);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void Harden(DbConnection connection) => HardenConnection(connection);

    internal static void HardenConnection(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite)
            return;

        // trusted_schema=OFF and cell_size_check=ON are honored via the PRAGMA interface and are
        // cheap idempotent guards against a hostile/corrupt schema or page.
        ExecutePragma(sqlite, "PRAGMA trusted_schema=OFF;");
        ExecutePragma(sqlite, "PRAGMA cell_size_check=ON;");

        // Defensive mode via the raw C API. Fail closed — an unhardened connection must not serve
        // queries, so surface the failure instead of swallowing it.
        var rc = SQLitePCL.raw.sqlite3_db_config(sqlite.Handle, SQLITE_DBCONFIG_DEFENSIVE, 1, out int applied);
        ThrowIfDefensiveNotApplied(rc, applied);
    }

    /// <summary>
    /// Fail-closed gate for the defensive-mode result. Extracted so it can be unit-tested without
    /// invoking the native API on a deliberately-broken handle (which is undefined behavior).
    /// </summary>
    internal static void ThrowIfDefensiveNotApplied(int rc, int applied)
    {
        if (rc != SQLitePCL.raw.SQLITE_OK || applied != 1)
            throw new InvalidOperationException(
                $"Failed to enable SQLite defensive mode on this connection (rc={rc}, applied={applied}).");
    }

    private static void ExecutePragma(SqliteConnection connection, string pragma)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = pragma;
        cmd.ExecuteNonQuery();
    }
}
