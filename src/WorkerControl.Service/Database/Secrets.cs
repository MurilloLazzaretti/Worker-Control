using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace WorkerControl.Service.Database;

/// <summary>
/// Keeps a password in the configuration file in a form only this machine reads back.
/// </summary>
public interface ISecretProtector
{
    /// <summary>
    /// False where there is nothing to protect with, and the password stays as it was typed.
    /// </summary>
    bool Available { get; }

    string Protect(string plain);

    /// <summary>
    /// Null when what is kept was not protected on this machine.
    /// </summary>
    string? Unprotect(string kept);
}

internal static class Secret
{
    public const string Prefix = "dpapi:";

    public static bool IsProtected(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>
/// The data protection of Windows, with the key of the machine: the service reads it back
/// under any account, and a copy of the file taken elsewhere does not.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WorkerControl.Database");

    public bool Available => true;

    public string Protect(string plain) =>
        Secret.Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine));

    public string? Unprotect(string kept)
    {
        try
        {
            var bytes = Convert.FromBase64String(kept[Secret.Prefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.LocalMachine));
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

internal sealed class NoSecretProtector : ISecretProtector
{
    public bool Available => false;

    public string Protect(string plain) => plain;

    public string? Unprotect(string kept) => null;
}
