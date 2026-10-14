using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using WorkerControl.Service.Database;

namespace WorkerControl.Service.Tests;

/// <summary>
/// Who may ask the service for anything, once it has an administration key.
/// </summary>
public sealed class AdminGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("admin-guard-").FullName;
    private static readonly byte[] Key = [.. Enumerable.Range(1, 32).Select(value => (byte)value)];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Ask(byte[] key, string command, DateTimeOffset? at = null, string? id = null) =>
        AdminSeal.Seal(key, new JObject { ["At"] = (at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(), ["Id"] = id ?? Guid.NewGuid().ToString("N"), ["Request"] = new JObject { ["Command"] = command } }.ToString());

    [Fact]
    public void What_is_sealed_is_the_same_at_both_ends()
    {
        // The ZapMQ seals the same text with the same key and nonce to the same bytes: the two ends are written apart.
        var sealedText = AdminSeal.Seal(Key, "{\"Command\":\"Status\"}", [.. Enumerable.Range(0, 12).Select(value => (byte)value)]);

        Assert.Equal("AAECAwQFBgcICQoLBBEtFsASKUHHOzd0vw2BuwoSQvilsABlawmIYzfslvlc5J/o", sealedText);
        Assert.Equal("{\"Command\":\"Status\"}", AdminSeal.Open(Key, sealedText));
        Assert.Null(AdminSeal.Open(RandomNumberGenerator.GetBytes(32), sealedText));
        Assert.Null(AdminSeal.Open(Key, "not base64 at all"));
        Assert.Null(AdminSeal.Open(Key, sealedText[..^6] + "AAAAA="));
    }

    [Fact]
    public void Only_what_was_sealed_with_the_key_now_and_once_is_opened()
    {
        var guard = new AdminGuard(_root, new NoSecretProtector(), TimeProvider.System);
        Assert.False(guard.Protected);
        Assert.Contains("32 bytes", guard.SetKey("c2hvcnQ="));
        Assert.Null(guard.SetKey(Convert.ToBase64String(Key)));
        Assert.True(guard.Protected);

        var asked = Ask(Key, "Status", id: "one");
        var opened = guard.Open(asked);
        Assert.Equal(("Status", "one"), ((string?)opened!.Request["Command"], opened.Id));
        // Sent again by whoever saw it go by.
        Assert.Null(guard.Open(asked));
        Assert.Null(guard.Open(Ask(RandomNumberGenerator.GetBytes(32), "Status")));
        Assert.Null(guard.Open(Ask(Key, "Status", DateTimeOffset.UtcNow.AddHours(-1))));
        Assert.Null(guard.Open(Ask(Key, "Status", DateTimeOffset.UtcNow.AddHours(1))));

        // The answer goes back sealed, tied to the question.
        var answer = JObject.Parse(AdminSeal.Open(Key, (string)guard.Seal(new JObject { ["Ok"] = true }, "one")["Sealed"]!)!);
        Assert.Equal(("one", true), ((string?)answer["Id"], (bool)answer["Answer"]!["Ok"]!));

        // The key is still there the next time the service starts.
        Assert.True(new AdminGuard(_root, new NoSecretProtector(), TimeProvider.System).Protected);
        File.WriteAllText(Path.Combine(_root, AdminGuard.File), "dpapi:not-of-this-machine");
        var broken = new AdminGuard(_root, new NoSecretProtector(), TimeProvider.System);
        Assert.False(broken.Protected);
        Assert.NotNull(broken.Problem);
    }

    [Fact]
    public async Task With_a_key_the_service_only_answers_what_is_sealed_with_it()
    {
        await using var rig = await Rig.StartAsync();
        Assert.False((bool)(await rig.CommandAsync("Status"))!["Protected"]!);

        var set = await rig.CommandAsync("SetAdminKey", request => request["Key"] = Convert.ToBase64String(Key));
        Assert.True(set!.Value<bool>("Ok"), set.ToString());

        // Whoever merely reaches the queue is not answered any more, whatever is asked.
        Assert.Equal("unauthenticated", (string?)(await rig.CommandAsync("Status"))!["Error"]!["Code"]);
        Assert.Equal("unauthenticated", (string?)(await rig.CommandAsync("SetAdminKey", request => request["Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))))!["Error"]!["Code"]);
        Assert.Equal("unauthenticated", (string?)(await rig.SealedAsync(Ask(RandomNumberGenerator.GetBytes(32), "Status")))!["Error"]!["Code"]);

        var answered = await rig.SealedAsync(Ask(Key, "Status", id: "q1"));
        var opened = JObject.Parse(AdminSeal.Open(Key, (string)answered!["Sealed"]!)!);
        Assert.Equal(("q1", true, true), ((string?)opened["Id"], (bool)opened["Answer"]!["Ok"]!, (bool)opened["Answer"]!["Protected"]!));
        // What 1.x asks is still answered as it was.
        Assert.NotNull(await rig.CurrentWorkersAsync());
        Assert.True(File.Exists(Path.Combine(rig.Directory, AdminGuard.File)));
    }
}
