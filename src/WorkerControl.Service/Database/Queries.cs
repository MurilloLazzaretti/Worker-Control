namespace WorkerControl.Service.Database;

/// <summary>
/// Every statement that is ever sent to the instance. All of them read what the instance says
/// about itself (its catalog, its management views and the history of backups and jobs) and
/// none reads a table of an application; a test holds this file to that.
/// </summary>
internal static class Queries
{
    /// <summary>
    /// Run on every connection: never wait long behind a lock, never win a deadlock, never hold a lock.
    /// </summary>
    public const string Preamble = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET LOCK_TIMEOUT 2000; SET DEADLOCK_PRIORITY LOW;";

    public const string Instance = """
        SELECT CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)),
               CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)),
               CAST(SERVERPROPERTY('ProductLevel') AS nvarchar(64)),
               CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
               @@VERSION
        """;

    public const string Machine = """
        SELECT DATEDIFF(SECOND, i.sqlserver_start_time, GETDATE()), i.cpu_count, i.physical_memory_kb
        FROM sys.dm_os_sys_info i
        """;

    public const string Cpu = """
        SELECT TOP (1)
               x.record.value('(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]', 'int'),
               x.record.value('(./Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]', 'int')
        FROM (SELECT b.[timestamp], CONVERT(xml, b.record) AS record
              FROM sys.dm_os_ring_buffers b
              WHERE b.ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR' AND b.record LIKE N'%<SystemHealth>%') AS x
        ORDER BY x.[timestamp] DESC
        """;

    public const string Memory = "SELECT m.physical_memory_in_use_kb FROM sys.dm_os_process_memory m";

    public const string Counters = """
        SELECT RTRIM(c.counter_name), RTRIM(c.instance_name), c.cntr_value
        FROM sys.dm_os_performance_counters c
        WHERE (c.object_name LIKE N'%:Memory Manager%' AND c.counter_name IN (N'Total Server Memory (KB)', N'Target Server Memory (KB)'))
           OR (c.object_name LIKE N'%:Buffer Manager%' AND c.counter_name = N'Page life expectancy')
           OR (c.object_name LIKE N'%:SQL Statistics%' AND c.counter_name = N'Batch Requests/sec')
           OR (c.object_name LIKE N'%:General Statistics%' AND c.counter_name = N'User Connections')
           OR (c.object_name LIKE N'%:Databases%' AND c.counter_name = N'Percent Log Used')
        """;

    public const string Databases = """
        SELECT d.name, d.state_desc, d.recovery_model_desc, d.user_access_desc, d.is_read_only, d.compatibility_level,
               CASE WHEN d.database_id <= 4 THEN 1 ELSE 0 END,
               ISNULL((SELECT SUM(CAST(f.size AS bigint)) * 8 FROM sys.master_files f WHERE f.database_id = d.database_id AND f.type = 0), 0),
               ISNULL((SELECT SUM(CAST(f.size AS bigint)) * 8 FROM sys.master_files f WHERE f.database_id = d.database_id AND f.type = 1), 0)
        FROM sys.databases d
        ORDER BY d.name
        """;

    public const string Sessions = """
        SELECT ISNULL(NULLIF(RTRIM(s.program_name), N''), N''), ISNULL(s.host_name, N''), ISNULL(s.login_name, N''), ISNULL(DB_NAME(s.database_id), N''),
               COUNT(*), SUM(CASE WHEN s.status = N'running' THEN 1 ELSE 0 END), SUM(CASE WHEN s.open_transaction_count > 0 THEN 1 ELSE 0 END)
        FROM sys.dm_exec_sessions s
        WHERE s.is_user_process = 1 AND s.session_id <> @@SPID
        GROUP BY ISNULL(NULLIF(RTRIM(s.program_name), N''), N''), ISNULL(s.host_name, N''), ISNULL(s.login_name, N''), ISNULL(DB_NAME(s.database_id), N'')
        """;

    /// <summary>
    /// What is running, and whoever keeps something else waiting even while doing nothing.
    /// </summary>
    public const string Activity = """
        SELECT TOP (200)
               s.session_id, ISNULL(r.status, s.status), ISNULL(r.command, N''),
               CAST(ISNULL(r.total_elapsed_time, 0) AS bigint), CAST(ISNULL(r.cpu_time, 0) AS bigint), CAST(ISNULL(r.logical_reads, 0) AS bigint),
               ISNULL(r.wait_type, N''), CAST(ISNULL(r.wait_time, 0) AS bigint), CAST(ISNULL(r.blocking_session_id, 0) AS int),
               ISNULL(DB_NAME(ISNULL(r.database_id, s.database_id)), N''),
               ISNULL(RTRIM(s.program_name), N''), ISNULL(s.host_name, N''), ISNULL(s.login_name, N''),
               s.open_transaction_count, CASE WHEN r.session_id IS NULL THEN 0 ELSE 1 END,
               LEFT(CASE WHEN r.statement_start_offset IS NULL THEN t.text
                         ELSE SUBSTRING(t.text, r.statement_start_offset / 2 + 1,
                              (CASE WHEN r.statement_end_offset = -1 THEN DATALENGTH(t.text) ELSE r.statement_end_offset END - r.statement_start_offset) / 2 + 1)
                    END, 4000)
        FROM sys.dm_exec_sessions s
        LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
        LEFT JOIN sys.dm_exec_connections c ON c.session_id = s.session_id AND c.parent_connection_id IS NULL
        OUTER APPLY sys.dm_exec_sql_text(ISNULL(r.sql_handle, c.most_recent_sql_handle)) t
        WHERE s.session_id <> @@SPID
          AND ((r.session_id IS NOT NULL AND s.is_user_process = 1)
               OR s.session_id IN (SELECT b.blocking_session_id FROM sys.dm_exec_requests b WHERE b.blocking_session_id > 0))
        ORDER BY ISNULL(r.total_elapsed_time, 0) DESC
        """;

    public const string Volumes = """
        SELECT DISTINCT v.volume_mount_point, ISNULL(v.logical_volume_name, N''), CAST(v.total_bytes AS bigint), CAST(v.available_bytes AS bigint)
        FROM sys.master_files f
        CROSS APPLY sys.dm_os_volume_stats(f.database_id, f.file_id) v
        """;

    public const string Backups = """
        SELECT b.database_name, b.type, DATEDIFF(MINUTE, MAX(b.backup_finish_date), GETDATE())
        FROM msdb.dbo.backupset b
        WHERE b.type IN ('D', 'I', 'L')
        GROUP BY b.database_name, b.type
        """;

    public const string Jobs = """
        SELECT j.name, j.enabled,
               CASE WHEN s.last_run_date > 0 THEN s.last_run_outcome END,
               CASE WHEN s.last_run_date > 0
                    THEN DATEDIFF(MINUTE,
                         CONVERT(datetime, CONVERT(char(8), s.last_run_date), 112)
                         + CONVERT(datetime, STUFF(STUFF(RIGHT('000000' + CONVERT(varchar(6), s.last_run_time), 6), 5, 0, ':'), 3, 0, ':'), 108),
                         GETDATE()) END,
               CASE WHEN s.last_run_date > 0
                    THEN s.last_run_duration / 10000 * 3600 + s.last_run_duration / 100 % 100 * 60 + s.last_run_duration % 100 END,
               LEFT(ISNULL(s.last_outcome_message, N''), 500)
        FROM msdb.dbo.sysjobs j
        JOIN msdb.dbo.sysjobservers s ON s.job_id = j.job_id
        ORDER BY j.name
        """;

    /// <summary>
    /// The statements that cost the most since the instance started, by processor, by time and by reads.
    /// </summary>
    public const string Expensive = """
        WITH q AS (
            SELECT qs.sql_handle, qs.statement_start_offset, qs.statement_end_offset, qs.execution_count,
                   qs.total_worker_time, qs.total_elapsed_time, qs.total_logical_reads, qs.max_elapsed_time,
                   DATEDIFF(MINUTE, qs.last_execution_time, GETDATE()) AS last_minutes,
                   ROW_NUMBER() OVER (ORDER BY qs.total_worker_time DESC) AS by_cpu,
                   ROW_NUMBER() OVER (ORDER BY qs.total_elapsed_time DESC) AS by_time,
                   ROW_NUMBER() OVER (ORDER BY qs.total_logical_reads DESC) AS by_reads
            FROM sys.dm_exec_query_stats qs)
        SELECT ISNULL(DB_NAME(t.dbid), N''),
               ISNULL(OBJECT_SCHEMA_NAME(t.objectid, t.dbid) + N'.' + OBJECT_NAME(t.objectid, t.dbid), N''),
               q.execution_count, q.total_worker_time, q.total_elapsed_time, q.total_logical_reads, q.max_elapsed_time, q.last_minutes,
               CASE WHEN q.by_cpu <= 25 THEN 1 ELSE 0 END, CASE WHEN q.by_time <= 25 THEN 1 ELSE 0 END, CASE WHEN q.by_reads <= 25 THEN 1 ELSE 0 END,
               LEFT(SUBSTRING(t.text, q.statement_start_offset / 2 + 1,
                    (CASE WHEN q.statement_end_offset = -1 THEN DATALENGTH(t.text) ELSE q.statement_end_offset END - q.statement_start_offset) / 2 + 1), 4000)
        FROM q
        CROSS APPLY sys.dm_exec_sql_text(q.sql_handle) t
        WHERE q.by_cpu <= 25 OR q.by_time <= 25 OR q.by_reads <= 25
        """;
}
