using Microsoft.Data.Sqlite;

namespace WorkerControl.Service.Database;

/// <summary>
/// What is known of an object since the last look: enough to tell whether it changed.
/// </summary>
public sealed record KnownObject(string Kind, string Schema, string Name, int Id, DateTime? ModifiedAt, string? Fingerprint);

/// <summary>
/// Something that happened to an object: Created, Altered, Renamed or Dropped.
/// </summary>
public sealed record ObjectChange
{
    public long Id { get; init; }
    public DateTimeOffset At { get; init; }
    public required string Database { get; init; }
    public required string Kind { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required string Action { get; init; }

    /// <summary>
    /// How the object was called before it was renamed.
    /// </summary>
    public string? OldName { get; init; }

    /// <summary>
    /// When the instance says it happened; what is known is only when it was noticed.
    /// </summary>
    public DateTime? ModifiedAt { get; init; }
    public string? OldFingerprint { get; init; }
    public string? NewFingerprint { get; init; }
    public string? Login { get; init; }
    public string? Host { get; init; }
    public string? Application { get; init; }

    /// <summary>
    /// The package of changes that brought it, when it was not done straight on the database.
    /// </summary>
    public string? Package { get; init; }

    /// <summary>
    /// Only given when one change is asked for.
    /// </summary>
    public string? OldScript { get; init; }
    public string? NewScript { get; init; }
}

public sealed record ChangeFilter(string? Database, string? Kind, string? Schema, string? Name, string? Search, DateTimeOffset? From, DateTimeOffset? To, int Limit);

/// <summary>
/// The objects of the databases as they were last seen, and every change noticed since, kept
/// in a SQLite file next to the service.
/// </summary>
internal sealed class ObjectHistory : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public static ObjectHistory? TryOpen(string directory, ILogger logger)
    {
        try
        {
            return new ObjectHistory(directory);
        }
        catch (Exception error)
        {
            logger.LogError("The changes to the objects of the database are not being kept, because their file could not be opened: {Error}", error.Message);
            return null;
        }
    }

    private ObjectHistory(string directory)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "objects.db"), Pooling = false }.ToString());
        _connection.Open();
        Run("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS object (
                db TEXT NOT NULL COLLATE NOCASE, kind TEXT NOT NULL, schema TEXT NOT NULL COLLATE NOCASE, name TEXT NOT NULL COLLATE NOCASE,
                object_id INTEGER NOT NULL, modified INTEGER, fingerprint TEXT, script TEXT,
                PRIMARY KEY (db, kind, schema, name));
            CREATE TABLE IF NOT EXISTS change (
                id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL,
                db TEXT NOT NULL COLLATE NOCASE, kind TEXT NOT NULL, schema TEXT NOT NULL COLLATE NOCASE, name TEXT NOT NULL COLLATE NOCASE,
                action TEXT NOT NULL, old_name TEXT, modified INTEGER, old_fingerprint TEXT, new_fingerprint TEXT, old_script TEXT, new_script TEXT,
                login TEXT, host TEXT, app TEXT);
            CREATE INDEX IF NOT EXISTS change_at ON change (at);
            CREATE INDEX IF NOT EXISTS change_object ON change (db, kind, schema, name);
            CREATE TABLE IF NOT EXISTS baseline (db TEXT PRIMARY KEY COLLATE NOCASE, at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS applied (
                db TEXT NOT NULL COLLATE NOCASE, kind TEXT, schema TEXT COLLATE NOCASE, name TEXT COLLATE NOCASE, package TEXT NOT NULL, at INTEGER NOT NULL);
            """);
        // A file from before a change said which package brought it.
        try
        {
            Run("ALTER TABLE change ADD COLUMN package TEXT");
        }
        catch (SqliteException)
        {
        }
    }

    private void Run(string text, params (string Name, object? Value)[] values)
    {
        lock (_gate)
        {
            using var command = Command(text, values);
            command.ExecuteNonQuery();
        }
    }

    private SqliteCommand Command(string text, params (string Name, object? Value)[] values)
    {
        var command = _connection.CreateCommand();
        command.CommandText = text;
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static long? Ticks(DateTime? moment) => moment is { } value ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds() : null;

    private static DateTime? Moment(SqliteDataReader row, int column) => row.IsDBNull(column) ? null : DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(column)).UtcDateTime;

    private static string? Maybe(SqliteDataReader row, int column) => row.IsDBNull(column) ? null : row.GetString(column);

    /// <summary>
    /// When the first look at a database ended. Until then, what is found is how it was, not a change.
    /// </summary>
    public DateTimeOffset? BaselineAt(string database)
    {
        lock (_gate)
        {
            using var command = Command("SELECT at FROM baseline WHERE db = $db", ("$db", database));
            return command.ExecuteScalar() is long at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null;
        }
    }

    public void MarkBaseline(string database, DateTimeOffset at) =>
        Run("INSERT OR REPLACE INTO baseline VALUES ($db, $at)", ("$db", database), ("$at", at.ToUnixTimeMilliseconds()));

    public List<KnownObject> Known(string database)
    {
        lock (_gate)
        {
            using var command = Command("SELECT kind, schema, name, object_id, modified, fingerprint FROM object WHERE db = $db", ("$db", database));
            using var reader = command.ExecuteReader();
            var known = new List<KnownObject>();
            while (reader.Read())
                known.Add(new KnownObject(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), Moment(reader, 4), Maybe(reader, 5)));
            return known;
        }
    }

    public string? Script(string database, string kind, string schema, string name)
    {
        lock (_gate)
        {
            using var command = Command("SELECT script FROM object WHERE db = $db AND kind = $kind AND schema = $schema AND name = $name",
                ("$db", database), ("$kind", kind), ("$schema", schema), ("$name", name));
            return command.ExecuteScalar() as string;
        }
    }

    public void Store(string database, CatalogObject item, string? fingerprint, string? script) =>
        Run("INSERT OR REPLACE INTO object VALUES ($db, $kind, $schema, $name, $id, $modified, $fingerprint, $script)",
            ("$db", database), ("$kind", item.Kind), ("$schema", item.Schema), ("$name", item.Name), ("$id", item.Id), ("$modified", Ticks(item.ModifiedAt)),
            ("$fingerprint", fingerprint), ("$script", script));

    /// <summary>
    /// The object is as it was; only when the instance says it was touched is new.
    /// </summary>
    public void Touch(string database, CatalogObject item) =>
        Run("UPDATE object SET object_id = $id, modified = $modified WHERE db = $db AND kind = $kind AND schema = $schema AND name = $name",
            ("$db", database), ("$kind", item.Kind), ("$schema", item.Schema), ("$name", item.Name), ("$id", item.Id), ("$modified", Ticks(item.ModifiedAt)));

    public void Forget(string database, string kind, string schema, string name) =>
        Run("DELETE FROM object WHERE db = $db AND kind = $kind AND schema = $schema AND name = $name", ("$db", database), ("$kind", kind), ("$schema", schema), ("$name", name));

    public long Add(ObjectChange change)
    {
        lock (_gate)
        {
            using var command = Command("""
                INSERT INTO change (at, db, kind, schema, name, action, old_name, modified, old_fingerprint, new_fingerprint, old_script, new_script, login, host, app, package)
                VALUES ($at, $db, $kind, $schema, $name, $action, $old, $modified, $oldPrint, $newPrint, $oldScript, $newScript, $login, $host, $app, $package);
                SELECT last_insert_rowid();
                """,
                ("$at", change.At.ToUnixTimeMilliseconds()), ("$db", change.Database), ("$kind", change.Kind), ("$schema", change.Schema), ("$name", change.Name),
                ("$action", change.Action), ("$old", change.OldName), ("$modified", Ticks(change.ModifiedAt)), ("$oldPrint", change.OldFingerprint), ("$newPrint", change.NewFingerprint),
                ("$oldScript", change.OldScript), ("$newScript", change.NewScript), ("$login", change.Login), ("$host", change.Host), ("$app", change.Application), ("$package", change.Package));
            return (long)command.ExecuteScalar()!;
        }
    }

    private const string Fields = "id, at, db, kind, schema, name, action, old_name, modified, old_fingerprint, new_fingerprint, login, host, app, package";

    private static ObjectChange Read(SqliteDataReader row) => new()
    {
        Id = row.GetInt64(0),
        At = DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(1)),
        Database = row.GetString(2),
        Kind = row.GetString(3),
        Schema = row.GetString(4),
        Name = row.GetString(5),
        Action = row.GetString(6),
        OldName = Maybe(row, 7),
        ModifiedAt = Moment(row, 8),
        OldFingerprint = Maybe(row, 9),
        NewFingerprint = Maybe(row, 10),
        Login = Maybe(row, 11),
        Host = Maybe(row, 12),
        Application = Maybe(row, 13),
        Package = Maybe(row, 14)
    };

    /// <summary>
    /// The changes that match, the latest first, without the scripts.
    /// </summary>
    public (List<ObjectChange> Changes, int Total) Changes(ChangeFilter filter)
    {
        var where = new List<string>();
        var values = new List<(string, object?)>();
        void Add(string condition, string name, object? value)
        {
            if (value is null or "")
                return;
            where.Add(condition);
            values.Add((name, value));
        }
        Add("db = $db", "$db", filter.Database);
        Add("kind = $kind", "$kind", filter.Kind);
        Add("schema = $schema", "$schema", filter.Schema);
        Add("name = $name", "$name", filter.Name);
        Add("(name LIKE $search ESCAPE '\\' OR old_name LIKE $search ESCAPE '\\' OR login LIKE $search ESCAPE '\\')", "$search",
            string.IsNullOrWhiteSpace(filter.Search) ? null : "%" + filter.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        Add("at >= $from", "$from", filter.From?.ToUnixTimeMilliseconds());
        Add("at <= $to", "$to", filter.To?.ToUnixTimeMilliseconds());
        var condition = where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", where);

        lock (_gate)
        {
            using var count = Command("SELECT COUNT(*) FROM change" + condition, [.. values]);
            var total = Convert.ToInt32(count.ExecuteScalar());
            using var command = Command($"SELECT {Fields} FROM change{condition} ORDER BY at DESC, id DESC LIMIT $limit", [.. values, ("$limit", Math.Clamp(filter.Limit, 1, 500))]);
            using var reader = command.ExecuteReader();
            var changes = new List<ObjectChange>();
            while (reader.Read())
                changes.Add(Read(reader));
            return (changes, total);
        }
    }

    public ObjectChange? Change(long id)
    {
        lock (_gate)
        {
            using var command = Command($"SELECT {Fields}, old_script, new_script FROM change WHERE id = $id", ("$id", id));
            using var reader = command.ExecuteReader();
            return reader.Read() ? Read(reader) with { OldScript = Maybe(reader, 15), NewScript = Maybe(reader, 16) } : null;
        }
    }

    /// <summary>
    /// Notes that a package was applied to a database: to one object, or, for a script, to
    /// whatever it touched. The next look at the objects says so of what it finds changed.
    /// </summary>
    public void NoteApplied(string database, string? kind, string? schema, string? name, string package, DateTimeOffset at) =>
        Run("INSERT INTO applied VALUES ($db, $kind, $schema, $name, $package, $at)",
            ("$db", database), ("$kind", kind), ("$schema", schema), ("$name", name), ("$package", package), ("$at", at.ToUnixTimeMilliseconds()));

    /// <summary>
    /// What packages did to a database up to a moment, and has not been looked at yet: the
    /// latest first. An entry without an object is of a script.
    /// </summary>
    public List<(string? Kind, string? Schema, string? Name, string Package)> Applied(string database, DateTimeOffset until)
    {
        lock (_gate)
        {
            using var command = Command("SELECT kind, schema, name, package FROM applied WHERE db = $db AND at <= $until ORDER BY at DESC", ("$db", database), ("$until", until.ToUnixTimeMilliseconds()));
            using var reader = command.ExecuteReader();
            var applied = new List<(string?, string?, string?, string)>();
            while (reader.Read())
                applied.Add((Maybe(reader, 0), Maybe(reader, 1), Maybe(reader, 2), reader.GetString(3)));
            return applied;
        }
    }

    public void ForgetApplied(string database, DateTimeOffset until) =>
        Run("DELETE FROM applied WHERE db = $db AND at <= $until", ("$db", database), ("$until", until.ToUnixTimeMilliseconds()));

    /// <summary>
    /// The objects whose script, as it was last seen, has a text in it: with how many times
    /// and the first lines it is on.
    /// </summary>
    public List<(string Kind, string Schema, string Name, string Script)> Search(string database, string text, int limit)
    {
        lock (_gate)
        {
            using var command = Command("SELECT kind, schema, name, script FROM object WHERE db = $db AND script LIKE $pattern ESCAPE '\\' ORDER BY kind, schema, name LIMIT $limit",
                ("$db", database), ("$pattern", "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"), ("$limit", limit));
            using var reader = command.ExecuteReader();
            var found = new List<(string, string, string, string)>();
            while (reader.Read())
                found.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            return found;
        }
    }

    public void Prune(DateTimeOffset before) => Run("DELETE FROM change WHERE at < $before", ("$before", before.ToUnixTimeMilliseconds()));

    public void Dispose() => _connection.Dispose();
}
