using Microsoft.Data.SqlClient;
using NKeeper.Plugins.Databases;

namespace NKeeper.Plugins.SqlServer;

/// <summary>The blocking manager: who blocks whom right now, as chains under their lead blockers; and KILL.</summary>
internal static class SqlBlocking
{
    /// <summary>Characters of a session's SQL sent.</summary>
    public const int MaxSqlChars = 4000;

    /// <summary>
    /// Every session that is blocked or blocking, or blocking itself: its session, its request (when
    /// running), and its current or last batch. A session's parallel threads waiting on each other only
    /// show in sys.dm_os_waiting_tasks, which is an error without VIEW SERVER STATE, so it's only read with it.
    /// </summary>
    private const string Query =
        """
        DECLARE @self TABLE (session_id smallint NOT NULL, wait_type nvarchar(60) NULL, wait_ms bigint NULL, wait_resource nvarchar(3072) NULL);
        IF HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') = 1
            INSERT @self EXEC sp_executesql N'SELECT session_id, wait_type, wait_duration_ms, resource_description FROM sys.dm_os_waiting_tasks WHERE blocking_session_id = session_id';

        WITH involved AS (
            SELECT session_id FROM sys.dm_exec_requests WHERE blocking_session_id <> 0
            UNION
            SELECT blocking_session_id FROM sys.dm_exec_requests WHERE blocking_session_id <> 0
            UNION
            SELECT session_id FROM @self
        )
        SELECT i.session_id, s.login_name, s.host_name, s.program_name,
               DB_NAME(COALESCE(r.database_id, s.database_id)), COALESCE(r.status, s.status),
               COALESCE(s.open_transaction_count, 0), NULLIF(r.blocking_session_id, 0), r.command,
               COALESCE(r.wait_type, w.wait_type),
               CASE WHEN r.wait_type IS NULL AND w.session_id IS NOT NULL THEN w.wait_ms ELSE CAST(r.wait_time AS bigint) END,
               COALESCE(NULLIF(r.wait_resource, N''), w.wait_resource),
               CAST(LEFT(t.text, @maxSql) AS nvarchar(max)),
               CAST(CASE WHEN w.session_id IS NOT NULL THEN 1 ELSE 0 END AS bit)
        FROM involved i
        LEFT JOIN sys.dm_exec_sessions s ON s.session_id = i.session_id
        OUTER APPLY (SELECT TOP 1 * FROM sys.dm_exec_requests rq WHERE rq.session_id = i.session_id ORDER BY rq.request_id) r
        OUTER APPLY (SELECT TOP 1 * FROM @self x WHERE x.session_id = i.session_id ORDER BY x.wait_ms DESC) w
        OUTER APPLY (SELECT TOP 1 most_recent_sql_handle FROM sys.dm_exec_connections c WHERE c.session_id = i.session_id) c
        OUTER APPLY sys.dm_exec_sql_text(COALESCE(r.sql_handle, c.most_recent_sql_handle)) t;

        SELECT CAST(HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') AS bit);
        """;

    public static async Task<BlockingReport> ReadAsync(SqlConnection connection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = new List<BlockingSession>();
        bool canSeeAll;
        await using (var command = new SqlCommand(Query, connection) { CommandTimeout = 30 })
        {
            command.Parameters.AddWithValue("@maxSql", MaxSqlChars);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                rows.Add(new BlockingSession(
                    reader.GetInt16(0),
                    Text(1),
                    Text(2),
                    Text(3),
                    Text(4),
                    Text(5),
                    reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetInt16(7),
                    Text(8),
                    Text(9),
                    reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    Text(11),
                    Text(12),
                    0,
                    0,
                    [],
                    Placeholder: reader.IsDBNull(1) && reader.IsDBNull(5),
                    SelfBlocking: reader.GetBoolean(13)));
            }

            await reader.NextResultAsync(cancellationToken);
            canSeeAll = await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0) && reader.GetBoolean(0);
        }

        return new BlockingReport(BlockingChains.Build(rows, Placeholder), BlockingChains.BlockedCount(rows), canSeeAll, now);
    }

    private static string? Placeholder(int sessionId) => sessionId < 0 ? Describe(sessionId) : null;

    private static string Describe(int sessionId) => sessionId switch
    {
        -2 => "orphaned distributed transaction",
        -3 => "deferred recovery transaction",
        -4 => "latch owner unknown",
        -5 => "asynchronous system task",
        _ => "not a session",
    };

    /// <summary>KILL takes no variables: the ID is a number, written in as one.</summary>
    public static async Task KillAsync(SqlConnection connection, int sessionId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"KILL {sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture)}", connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
