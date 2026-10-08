namespace WorkerControl.Service.Database;

/// <summary>
/// How far the watching of the objects of one database has got.
/// </summary>
public sealed record ObjectTracking(string Database, int Known, int Total, DateTimeOffset? BaselineAt, DateTimeOffset? ScannedAt, string? Problem);

/// <summary>
/// Notices what changed in the objects of a database between one look and the next.
///
/// Each look lists the objects, which is cheap, and reads again only the ones the instance
/// says were touched since the last time. An object is the same while the script that creates
/// it is: a rebuilt index or a recompiled procedure is no change. The first look at a database
/// only records how it is.
/// </summary>
internal sealed class ObjectWatcher(IDatabaseSource source, ObjectHistory history, TimeProvider time)
{
    /// <summary>
    /// Between one object and the next, so that reading a whole database is never a burden on it.
    /// </summary>
    public TimeSpan Pause { get; init; } = TimeSpan.FromMilliseconds(20);

    private static string Key(string kind, string schema, string name) => $"{kind}|{schema}|{name}".ToLowerInvariant();

    /// <summary>
    /// One look at one database. Gives the changes it noticed.
    /// </summary>
    public async Task<(ObjectTracking Tracking, List<ObjectChange> Changes)> ScanAsync(DatabaseConnection connection, string database, CancellationToken stopping, Action<ObjectTracking>? progress = null)
    {
        var now = time.GetUtcNow();
        var listed = await source.ObjectsAsync(connection, database, stopping);
        var baseline = history.BaselineAt(database);
        var known = history.Known(database).ToDictionary(item => Key(item.Kind, item.Schema, item.Name));
        var byId = known.Values.GroupBy(item => (item.Kind, item.Id)).ToDictionary(group => group.Key, group => group.First());
        var present = listed.Select(item => Key(item.Kind, item.Schema, item.Name)).ToHashSet();
        var changes = new List<ObjectChange>();
        var read = 0;
        var looked = 0;

        async Task<ChangeAuthor?> Who(int id) => await source.WhoChangedAsync(connection, database, id, stopping);

        foreach (var item in listed)
        {
            stopping.ThrowIfCancellationRequested();
            // The first look at a big database takes minutes; whoever waits is told how far it is.
            if (looked++ % 25 == 0)
                progress?.Invoke(new ObjectTracking(database, looked - 1, listed.Count, baseline, null, null));
            var key = Key(item.Kind, item.Schema, item.Name);
            known.TryGetValue(key, out var before);
            if (before is not null && before.Id == item.Id && before.ModifiedAt == item.ModifiedAt)
                continue;

            if (read++ > 0 && Pause > TimeSpan.Zero)
                await Task.Delay(Pause, stopping);
            var detail = await source.ObjectAsync(connection, database, item, stopping);

            if (before is not null)
            {
                if (before.Fingerprint == detail.Fingerprint)
                {
                    history.Touch(database, item);
                    continue;
                }
                var author = await Who(item.Id);
                changes.Add(new ObjectChange
                {
                    At = now, Database = database, Kind = item.Kind, Schema = item.Schema, Name = item.Name, Action = "Altered", ModifiedAt = item.ModifiedAt,
                    OldFingerprint = before.Fingerprint, NewFingerprint = detail.Fingerprint, OldScript = history.Script(database, item.Kind, item.Schema, item.Name), NewScript = detail.Script,
                    Login = author?.Login, Host = author?.Host, Application = author?.Application
                });
            }
            else if (baseline is not null)
            {
                // The same object under another name was renamed, not made.
                byId.TryGetValue((item.Kind, item.Id), out var renamed);
                if (renamed is not null && present.Contains(Key(renamed.Kind, renamed.Schema, renamed.Name)))
                    renamed = null;
                var author = await Who(item.Id);
                changes.Add(new ObjectChange
                {
                    At = now, Database = database, Kind = item.Kind, Schema = item.Schema, Name = item.Name, Action = renamed is null ? "Created" : "Renamed",
                    OldName = renamed is null ? null : renamed.Schema + "." + renamed.Name, ModifiedAt = item.ModifiedAt,
                    OldFingerprint = renamed?.Fingerprint, NewFingerprint = detail.Fingerprint,
                    OldScript = renamed is null ? null : history.Script(database, renamed.Kind, renamed.Schema, renamed.Name), NewScript = detail.Script,
                    Login = author?.Login, Host = author?.Host, Application = author?.Application
                });
                if (renamed is not null)
                {
                    history.Forget(database, renamed.Kind, renamed.Schema, renamed.Name);
                    known.Remove(Key(renamed.Kind, renamed.Schema, renamed.Name));
                }
            }
            history.Store(database, item, detail.Fingerprint, detail.Script);
        }

        foreach (var gone in known.Values.Where(item => !present.Contains(Key(item.Kind, item.Schema, item.Name))))
        {
            if (baseline is not null)
            {
                var author = await Who(gone.Id);
                changes.Add(new ObjectChange
                {
                    At = now, Database = database, Kind = gone.Kind, Schema = gone.Schema, Name = gone.Name, Action = "Dropped",
                    OldFingerprint = gone.Fingerprint, OldScript = history.Script(database, gone.Kind, gone.Schema, gone.Name),
                    Login = author?.Login, Host = author?.Host, Application = author?.Application
                });
            }
            history.Forget(database, gone.Kind, gone.Schema, gone.Name);
        }

        var saved = changes.Select(change => change with { Id = history.Add(change), OldScript = null, NewScript = null }).ToList();
        if (baseline is null)
        {
            baseline = now;
            history.MarkBaseline(database, now);
        }
        return (new ObjectTracking(database, listed.Count, listed.Count, baseline, now, null), saved);
    }
}
