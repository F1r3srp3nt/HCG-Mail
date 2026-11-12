using System.Text;
using Foundation;
using HCG.Mail.Core.Security;
using Security;

namespace HCG.Mail.iOS.Platform;

/// <summary>
/// IMAP/SMTP passwords in the iOS Keychain as generic-password items (service "HCG Mail", account = credential key).
/// Items are readable only while the device is unlocked, never leave this device (no iCloud Keychain sync, not
/// restored to another device from a backup) and are never written to a file. Uses the Security framework directly;
/// no command-line tools are involved.
/// </summary>
public sealed class IosKeychainCredentialVault : ICredentialVault
{
    public const string Service = "HCG Mail";

    public bool IsPersistent => true;

    public string StorageDescription => "iOS Keychain (this device only)";

    private static SecRecord Query(string key) => new(SecKind.GenericPassword)
    {
        Service = Service,
        Account = key,
        Synchronizable = false,
    };

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var data = SecKeyChain.QueryAsData(Query(key), false, out var status);
        return status switch
        {
            SecStatusCode.Success => Task.FromResult<string?>(data is null ? null : Encoding.UTF8.GetString(data.ToArray())),
            SecStatusCode.ItemNotFound => Task.FromResult<string?>(null),
            _ => throw Fail("read", status),
        };
    }

    public Task SetAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        using var value = NSData.FromArray(Encoding.UTF8.GetBytes(secret));
        var changes = new SecRecord(SecKind.GenericPassword)
        {
            ValueData = value,
            Accessible = SecAccessible.WhenUnlockedThisDeviceOnly,
        };
        var status = SecKeyChain.Update(Query(key), changes);
        if (status == SecStatusCode.ItemNotFound)
        {
            var record = Query(key);
            record.ValueData = value;
            record.Accessible = SecAccessible.WhenUnlockedThisDeviceOnly;
            status = SecKeyChain.Add(record);
        }
        if (status != SecStatusCode.Success)
            throw Fail("save", status);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var status = SecKeyChain.Remove(Query(key));
        if (status is not (SecStatusCode.Success or SecStatusCode.ItemNotFound))
            throw Fail("delete", status);
        return Task.CompletedTask;
    }

    private static CredentialVaultException Fail(string action, SecStatusCode status) =>
        new($"Could not {action} the password in the iOS Keychain ({status}). If the device is locked, unlock it and try again.");
}
