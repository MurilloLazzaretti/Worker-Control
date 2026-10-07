using System.Text.Json;
using WorkerControl.Core;

namespace WorkerControl.Service;

/// <summary>
/// The workers under supervision, kept on disk so that a restarted service finds them again
/// instead of starting duplicates.
/// </summary>
internal sealed class StateStore(string directory, ILogger logger)
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private readonly string _path = Path.Combine(directory, "state.json");

    public IReadOnlyList<WorkerRecord> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<WorkerRecord>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning("The record of running workers could not be read and is being ignored: {Error}", error.Message);
            return [];
        }
    }

    public void Save(IReadOnlyList<WorkerRecord> records)
    {
        try
        {
            // Written aside and moved into place, so a crash never leaves half a file.
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(records, Format));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The record of running workers could not be written: {Error}", error.Message);
        }
    }
}
