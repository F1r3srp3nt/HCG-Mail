using System.Collections.Concurrent;
using HCG.Mail.Core.Security;

namespace HCG.Mail.Infrastructure.Credentials;

/// <summary>
/// Keeps secrets in process memory only. Used where no supported OS credential store is
/// available (e.g. Linux development containers) — the password is asked for again after a
/// restart rather than being written anywhere insecurely.
/// </summary>
public sealed class SessionCredentialVault : ICredentialVault
{
    private readonly ConcurrentDictionary<string, string> _secrets = new();

    public bool IsPersistent => false;

    public string StorageDescription => "this session only (no secure storage available on this platform)";

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_secrets.TryGetValue(key, out var secret) ? secret : null);

    public Task SetAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        _secrets[key] = secret;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _secrets.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

public static class CredentialVaultFactory
{
    public static ICredentialVault CreateForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsCredentialVault();
        if (OperatingSystem.IsMacOS())
            return new MacKeychainCredentialVault();
        return new SessionCredentialVault();
    }
}
