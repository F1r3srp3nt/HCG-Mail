using System.Security.Cryptography;
using System.Text;
using HCG.Mail.Core.Security;
using Microsoft.Extensions.Logging;

namespace HCG.Mail.Infrastructure.Security;

/// <summary>
/// Encrypts an account's local mail data (offline copies, Outbox) with AES-256-GCM. Each account and purpose has its own
/// random 256-bit key, created on first use and kept only in the operating system's credential store (Windows Credential
/// Manager, macOS/iOS Keychain) — never in a file, and never next to the data it protects. The associated data binds every
/// file to its account, purpose and place, so a file copied into another account's folder, or to another slot, does not
/// decrypt (protection against cross-account access). Deleting the key makes every remaining copy unreadable.
/// </summary>
public sealed class AccountDataProtector
{
    private static readonly byte[] Magic = "HCGP1"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly ICredentialVault _vault;
    private readonly ILogger _logger;
    private readonly Dictionary<string, byte[]> _keys = [];

    public AccountDataProtector(ICredentialVault vault, ILogger<AccountDataProtector> logger)
    {
        _vault = vault;
        _logger = logger;
    }

    public static bool IsSupported => AesGcm.IsSupported;

    /// <summary>True when keys survive a restart (a persistent credential store); otherwise data lasts for this session only.</summary>
    public bool KeysArePersistent => _vault.IsPersistent;

    public string KeyStoreDescription => _vault.StorageDescription;

    public static string KeyName(string purpose, string accountId) => $"{purpose}:{accountId}";

    public byte[] Protect(string purpose, string accountId, string slot, ReadOnlySpan<byte> plaintext)
    {
        var key = GetKey(purpose, accountId, create: true)!;
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var output = new byte[Magic.Length + NonceSize + TagSize + plaintext.Length];
        Magic.CopyTo(output, 0);
        nonce.CopyTo(output, Magic.Length);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(Magic.Length + NonceSize + TagSize), output.AsSpan(Magic.Length + NonceSize, TagSize),
            AssociatedData(purpose, accountId, slot));
        return output;
    }

    /// <summary>Returns null when the data is not ours, was moved, altered, or its key is gone.</summary>
    public byte[]? Unprotect(string purpose, string accountId, string slot, ReadOnlySpan<byte> data)
    {
        if (data.Length < Magic.Length + NonceSize + TagSize || !data[..Magic.Length].SequenceEqual(Magic))
            return null;
        var key = GetKey(purpose, accountId, create: false);
        if (key is null)
            return null;
        var plaintext = new byte[data.Length - Magic.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(data.Slice(Magic.Length, NonceSize), data[(Magic.Length + NonceSize + TagSize)..], data.Slice(Magic.Length + NonceSize, TagSize),
                plaintext, AssociatedData(purpose, accountId, slot));
            return plaintext;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    /// <summary>Deletes the key: everything it protected becomes unreadable (crypto-shredding).</summary>
    public void DeleteKey(string purpose, string accountId)
    {
        var name = KeyName(purpose, accountId);
        lock (_keys)
            _keys.Remove(name);
        try
        {
            Run(() => _vault.DeleteAsync(name));
        }
        catch (Exception ex) when (ex is CredentialVaultException or InvalidOperationException)
        {
            _logger.LogWarning("Could not delete the {Purpose} key of account {AccountId} ({Error})", purpose, accountId, ex.GetType().Name);
        }
    }

    private byte[]? GetKey(string purpose, string accountId, bool create)
    {
        var name = KeyName(purpose, accountId);
        lock (_keys)
        {
            if (_keys.TryGetValue(name, out var cached))
                return cached;
            var stored = Run(() => _vault.GetAsync(name));
            byte[]? key = null;
            if (!string.IsNullOrEmpty(stored))
            {
                try
                {
                    key = Convert.FromBase64String(stored);
                }
                catch (FormatException)
                {
                    key = null;
                }
                if (key is { Length: not 32 })
                    key = null;
            }
            if (key is null && create)
            {
                key = RandomNumberGenerator.GetBytes(32);
                Run(() => _vault.SetAsync(name, Convert.ToBase64String(key)));
            }
            if (key is not null)
                _keys[name] = key;
            return key;
        }
    }

    // The vaults complete synchronously; Task.Run keeps a UI synchronization context out of the wait.
    private static void Run(Func<Task> action) => Task.Run(action).GetAwaiter().GetResult();

    private static T Run<T>(Func<Task<T>> action) => Task.Run(action).GetAwaiter().GetResult();

    private static byte[] AssociatedData(string purpose, string accountId, string slot) =>
        Encoding.UTF8.GetBytes($"HCG Mail\u001F{purpose}\u001F{accountId}\u001F{slot}");
}
