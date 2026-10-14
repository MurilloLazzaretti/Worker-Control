using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using WorkerControl.Service.Database;

namespace WorkerControl.Service;

/// <summary>
/// Sealing and opening what goes over the administration queue: AES-256-GCM with a key only
/// the two ends have. What is sealed is the base64 of the nonce (12 bytes), the tag (16) and
/// the text.
/// </summary>
public static class AdminSeal
{
    public const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static string Seal(byte[] key, string plain, byte[]? nonce = null)
    {
        nonce ??= RandomNumberGenerator.GetBytes(NonceBytes);
        var text = Encoding.UTF8.GetBytes(plain);
        var sealedBytes = new byte[NonceBytes + TagBytes + text.Length];
        nonce.CopyTo(sealedBytes, 0);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, text, sealedBytes.AsSpan(NonceBytes + TagBytes), sealedBytes.AsSpan(NonceBytes, TagBytes));
        return Convert.ToBase64String(sealedBytes);
    }

    /// <summary>
    /// Null when it was not sealed with this key, or was changed on the way.
    /// </summary>
    public static string? Open(byte[] key, string sealedText)
    {
        try
        {
            var bytes = Convert.FromBase64String(sealedText);
            if (bytes.Length < NonceBytes + TagBytes)
                return null;
            var plain = new byte[bytes.Length - NonceBytes - TagBytes];
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(bytes.AsSpan(0, NonceBytes), bytes.AsSpan(NonceBytes + TagBytes), bytes.AsSpan(NonceBytes, TagBytes), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Who may ask this service for anything. With a key, only what was sealed with it is taken:
/// whoever merely reaches the broker cannot ask, read what is asked, nor ask again something
/// that was asked before. Without one, anybody who reaches the queue is answered, as it was.
/// </summary>
internal sealed class AdminGuard
{
    public const string File = "admin.key";

    /// <summary>
    /// How far the clock of whoever asks may be from this one.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();
    private byte[]? _key;

    public AdminGuard(string directory, ISecretProtector protector, TimeProvider time)
    {
        _path = Path.Combine(directory, File);
        _protector = protector;
        _time = time;
        Load();
    }

    public bool Protected => _key is not null;

    /// <summary>
    /// Why the key that is on the disk could not be read, when that is the case: then nothing is taken at all.
    /// </summary>
    public string? Problem { get; private set; }

    private void Load()
    {
        if (!System.IO.File.Exists(_path))
            return;
        try
        {
            var kept = System.IO.File.ReadAllText(_path).Trim();
            var text = Secret.IsProtected(kept) ? _protector.Unprotect(kept) : kept;
            var key = text is null ? null : Convert.FromBase64String(text);
            if (key is { Length: AdminSeal.KeyBytes })
                _key = key;
            else
                Problem = $"The key in {File} could not be read on this machine";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            Problem = $"The key in {File} could not be read: {error.Message}";
        }
    }

    /// <summary>
    /// Takes the key from now on, and keeps it in a form only this machine reads back where that can be done.
    /// </summary>
    public string? SetKey(string? given)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(given ?? "");
        }
        catch (FormatException)
        {
            return "\"Key\" must be 32 bytes in base64";
        }
        if (key.Length != AdminSeal.KeyBytes)
            return "\"Key\" must be 32 bytes in base64";
        try
        {
            var text = Convert.ToBase64String(key);
            System.IO.File.WriteAllText(_path, _protector.Available ? _protector.Protect(text) : text);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "The key could not be kept: " + error.Message;
        }
        _key = key;
        Problem = null;
        return null;
    }

    public sealed record Opened(JObject Request, string Id);

    /// <summary>
    /// The request inside what was sealed. Null when it was not sealed with the key of this
    /// service, is too old or too new, or was already asked once.
    /// </summary>
    public Opened? Open(string sealedText)
    {
        if (_key is null || AdminSeal.Open(_key, sealedText) is not { } plain)
            return null;
        JObject envelope;
        try
        {
            envelope = JObject.Parse(plain);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
        if (envelope.Value<long?>("At") is not { } at || envelope.Value<string>("Id") is not { Length: > 0 } id || envelope["Request"] is not JObject request)
            return null;

        var now = _time.GetUtcNow();
        if ((now - DateTimeOffset.FromUnixTimeMilliseconds(at)).Duration() > Window)
            return null;
        foreach (var old in _seen.Where(pair => now - pair.Value > Window + Window).Select(pair => pair.Key).ToList())
            _seen.TryRemove(old, out _);
        // The same request twice is somebody sending again what they saw go by.
        return _seen.TryAdd(id, now) ? new Opened(request, id) : null;
    }

    /// <summary>
    /// The answer, sealed for whoever asked, with the identity of the question so that it cannot be given as the answer to another.
    /// </summary>
    public JObject Seal(JObject answer, string id) => new()
    {
        ["Sealed"] = AdminSeal.Seal(_key!, new JObject { ["Id"] = id, ["Answer"] = answer }.ToString(Newtonsoft.Json.Formatting.None))
    };
}
