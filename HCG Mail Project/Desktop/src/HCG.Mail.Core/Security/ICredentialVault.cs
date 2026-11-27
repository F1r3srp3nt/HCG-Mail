namespace HCG.Mail.Core.Security;

/// <summary>
/// Stores account secrets in the operating system's protected credential store
/// (Windows Credential Manager, macOS Keychain). Implementations must never write secrets to
/// plaintext files. A vault that cannot persist securely keeps secrets in memory only and
/// reports <see cref="IsPersistent"/> = false so the UI can say so.
/// </summary>
public interface ICredentialVault
{
    /// <summary>True when secrets survive an application restart.</summary>
    bool IsPersistent { get; }

    /// <summary>Human-readable name of the backing store, shown in the account screen.</summary>
    string StorageDescription { get; }

    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(string key, string secret, CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class CredentialVaultException(string message, Exception? inner = null) : Exception(message, inner);
