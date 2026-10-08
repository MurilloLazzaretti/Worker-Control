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
        SELECT DISTINCT v.volume_mount_point, ISNULL(v.logical_volume_name, N''), CAST(v.total_bytes AS bigint), CAST(v.available_bytes AS bigint), ISNULL(DB_NAME(f.database_id), N'')
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
    /// The statements that cost the most since the instance started, by processor, by time and by
    /// reads. Only what ran in a database of an application: what any monitor asks of the
    /// instance, this one included, runs in the system databases. <c>@names</c> narrows it to
    /// some databases, written as <c>|one|another|</c>; empty takes them all.
    /// </summary>
    public const string Expensive = """
        WITH q AS (
            SELECT qs.sql_handle, qs.statement_start_offset, qs.statement_end_offset, qs.execution_count,
                   qs.total_worker_time, qs.total_elapsed_time, qs.total_logical_reads, qs.max_elapsed_time,
                   DATEDIFF(MINUTE, qs.last_execution_time, GETDATE()) AS last_minutes, d.dbid,
                   ROW_NUMBER() OVER (ORDER BY qs.total_worker_time DESC) AS by_cpu,
                   ROW_NUMBER() OVER (ORDER BY qs.total_elapsed_time DESC) AS by_time,
                   ROW_NUMBER() OVER (ORDER BY qs.total_logical_reads DESC) AS by_reads
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY (SELECT CONVERT(int, pa.value) AS dbid FROM sys.dm_exec_plan_attributes(qs.plan_handle) pa WHERE pa.attribute = N'dbid') d
            WHERE d.dbid > 4 AND d.dbid < 32767
              AND (@names = N'' OR CHARINDEX(N'|' + LOWER(DB_NAME(d.dbid)) + N'|', @names) > 0))
        SELECT ISNULL(DB_NAME(q.dbid), N''),
               ISNULL(OBJECT_SCHEMA_NAME(t.objectid, t.dbid) + N'.' + OBJECT_NAME(t.objectid, t.dbid), N''),
               q.execution_count, q.total_worker_time, q.total_elapsed_time, q.total_logical_reads, q.max_elapsed_time, q.last_minutes,
               CASE WHEN q.by_cpu <= 25 THEN 1 ELSE 0 END, CASE WHEN q.by_time <= 25 THEN 1 ELSE 0 END, CASE WHEN q.by_reads <= 25 THEN 1 ELSE 0 END,
               LEFT(SUBSTRING(t.text, q.statement_start_offset / 2 + 1,
                    (CASE WHEN q.statement_end_offset = -1 THEN DATALENGTH(t.text) ELSE q.statement_end_offset END - q.statement_start_offset) / 2 + 1), 4000)
        FROM q
        CROSS APPLY sys.dm_exec_sql_text(q.sql_handle) t
        WHERE q.by_cpu <= 25 OR q.by_time <= 25 OR q.by_reads <= 25
        """;

    // ---------------------------------------------------------------- the objects of a database
    //
    // Run with the database as the one of the connection. Dates are given in universal time.

    private const string Utc = "DATEDIFF(MINUTE, GETDATE(), GETUTCDATE())";

    public const string Collation = "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))";

    /// <summary>
    /// Tables, views, procedures and functions. Rows and size come from what the instance
    /// counts by itself, without looking at the table.
    /// </summary>
    public const string Objects = $"""
        SELECT o.object_id, SCHEMA_NAME(o.schema_id), o.name, RTRIM(o.type),
               DATEADD(MINUTE, {Utc}, o.create_date), DATEADD(MINUTE, {Utc}, o.modify_date),
               (SELECT SUM(p.row_count) FROM sys.dm_db_partition_stats p WHERE p.object_id = o.object_id AND p.index_id IN (0, 1)),
               (SELECT SUM(p.reserved_page_count) * 8 FROM sys.dm_db_partition_stats p WHERE p.object_id = o.object_id)
        FROM sys.objects o
        WHERE o.is_ms_shipped = 0 AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF')
        """;

    /// <summary>
    /// The same without rows and sizes, for a user that may not read them.
    /// </summary>
    public const string ObjectsPlain = $"""
        SELECT o.object_id, SCHEMA_NAME(o.schema_id), o.name, RTRIM(o.type),
               DATEADD(MINUTE, {Utc}, o.create_date), DATEADD(MINUTE, {Utc}, o.modify_date), NULL, NULL
        FROM sys.objects o
        WHERE o.is_ms_shipped = 0 AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF')
        """;

    public const string Types = $"""
        SELECT t.user_type_id, SCHEMA_NAME(t.schema_id), t.name, CASE WHEN t.is_table_type = 1 THEN 'TT' ELSE 'T' END,
               DATEADD(MINUTE, {Utc}, o.create_date), DATEADD(MINUTE, {Utc}, o.modify_date), NULL, NULL
        FROM sys.types t
        LEFT JOIN sys.table_types tt ON tt.user_type_id = t.user_type_id
        LEFT JOIN sys.objects o ON o.object_id = tt.type_table_object_id
        WHERE t.is_user_defined = 1
        """;

    /// <summary>
    /// A type that is not a table: what it is made from. And the table behind one that is.
    /// </summary>
    public const string TypeBase = """
        SELECT b.name, t.max_length, t.precision, t.scale, t.is_nullable, tt.type_table_object_id
        FROM sys.types t
        LEFT JOIN sys.types b ON b.user_type_id = t.system_type_id AND b.is_user_defined = 0
        LEFT JOIN sys.table_types tt ON tt.user_type_id = t.user_type_id
        WHERE t.user_type_id = @id
        """;

    public const string Columns = """
        SELECT c.name, t.name, CASE WHEN t.is_user_defined = 1 THEN SCHEMA_NAME(t.schema_id) END, c.max_length, c.precision, c.scale,
               c.is_nullable, c.is_identity, CONVERT(nvarchar(40), ic.seed_value), CONVERT(nvarchar(40), ic.increment_value),
               d.name, d.definition, d.is_system_named, cc.definition, ISNULL(cc.is_persisted, 0), c.collation_name
        FROM sys.columns c
        JOIN sys.types t ON t.user_type_id = c.user_type_id
        LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        LEFT JOIN sys.default_constraints d ON d.object_id = c.default_object_id
        LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        WHERE c.object_id = @id
        ORDER BY c.column_id
        """;

    /// <summary>
    /// The parameter numbered zero is what a function gives back.
    /// </summary>
    public const string Parameters = """
        SELECT p.parameter_id, p.name, t.name, CASE WHEN t.is_user_defined = 1 THEN SCHEMA_NAME(t.schema_id) END, p.max_length, p.precision, p.scale,
               p.is_output, p.is_readonly
        FROM sys.parameters p
        JOIN sys.types t ON t.user_type_id = p.user_type_id
        WHERE p.object_id = @id
        ORDER BY p.parameter_id
        """;

    public const string Definition = "SELECT m.definition FROM sys.sql_modules m WHERE m.object_id = @id";

    public const string Indexes = """
        SELECT i.index_id, i.name, i.type_desc, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.is_disabled, i.filter_definition,
               c.name, ic.is_descending_key, ic.is_included_column
        FROM sys.indexes i
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = @id AND i.type > 0 AND i.is_hypothetical = 0
        ORDER BY i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id
        """;

    public const string ForeignKeys = """
        SELECT fk.name, pc.name, SCHEMA_NAME(rt.schema_id), rt.name, rc.name,
               fk.delete_referential_action_desc, fk.update_referential_action_desc, fk.is_disabled
        FROM sys.foreign_keys fk
        JOIN sys.foreign_key_columns k ON k.constraint_object_id = fk.object_id
        JOIN sys.columns pc ON pc.object_id = k.parent_object_id AND pc.column_id = k.parent_column_id
        JOIN sys.columns rc ON rc.object_id = k.referenced_object_id AND rc.column_id = k.referenced_column_id
        JOIN sys.objects rt ON rt.object_id = fk.referenced_object_id
        WHERE fk.parent_object_id = @id
        ORDER BY fk.name, k.constraint_column_id
        """;

    public const string Checks = """
        SELECT k.name, k.definition, k.is_disabled
        FROM sys.check_constraints k
        WHERE k.parent_object_id = @id
        ORDER BY k.name
        """;

    public const string Triggers = """
        SELECT t.name, t.is_disabled, t.is_instead_of_trigger, m.definition
        FROM sys.triggers t
        LEFT JOIN sys.sql_modules m ON m.object_id = t.object_id
        WHERE t.parent_id = @id
        ORDER BY t.name
        """;

    /// <summary>
    /// What the object names in its text. What it names in another database, or what is not
    /// there any more, comes without a kind.
    /// </summary>
    public const string Uses = """
        SELECT DISTINCT ISNULL(d.referenced_schema_name, ISNULL(SCHEMA_NAME(o.schema_id), SCHEMA_NAME(t.schema_id))), d.referenced_entity_name,
               CASE WHEN d.referenced_class = 6 THEN CASE WHEN t.is_table_type = 1 THEN 'TT' ELSE 'T' END ELSE RTRIM(o.type) END,
               d.referenced_database_name
        FROM sys.sql_expression_dependencies d
        LEFT JOIN sys.objects o ON o.object_id = d.referenced_id AND d.referenced_class = 1
        LEFT JOIN sys.types t ON t.user_type_id = d.referenced_id AND d.referenced_class = 6
        WHERE d.referencing_id = @id AND d.referenced_entity_name IS NOT NULL
        """;

    public const string UsedBy = """
        SELECT DISTINCT SCHEMA_NAME(o.schema_id), o.name, RTRIM(o.type), CAST(NULL AS nvarchar(128))
        FROM sys.sql_expression_dependencies d
        JOIN sys.objects o ON o.object_id = d.referencing_id
        WHERE d.referenced_id = @id AND d.referenced_class = 1 AND o.is_ms_shipped = 0
        """;

    /// <summary>
    /// Who uses a type: the tables with a column of it and whatever takes a parameter of it.
    /// </summary>
    public const string TypeUsedBy = """
        SELECT DISTINCT SCHEMA_NAME(o.schema_id), o.name, RTRIM(o.type), CAST(NULL AS nvarchar(128))
        FROM sys.objects o
        WHERE o.is_ms_shipped = 0 AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF')
          AND (EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = o.object_id AND c.user_type_id = @id)
               OR EXISTS (SELECT 1 FROM sys.parameters p WHERE p.object_id = o.object_id AND p.user_type_id = @id))
        """;
}
