using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using HCG.Mail.Core.Security;

namespace HCG.Mail.Infrastructure.Credentials;

/// <summary>
/// macOS login Keychain via Security.framework generic-password items (service "HCG Mail").
/// The password never appears on a command line or in a file. Items are visible in
/// Keychain Access under the name "HCG Mail".
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainCredentialVault : ICredentialVault
{
    private const string Service = "HCG Mail";
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;

    public bool IsPersistent => true;

    public string StorageDescription => "macOS Keychain";

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(key);
        var status = Native.SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
            out var length, out var data, out var item);
        if (status == ErrSecItemNotFound)
            return Task.FromResult<string?>(null);
        Check(status, "read the password from the Keychain");

        try
        {
            var bytes = new byte[length];
            if (length > 0)
                Marshal.Copy(data, bytes, 0, (int)length);
            var secret = Encoding.UTF8.GetString(bytes);
            Array.Clear(bytes);
            return Task.FromResult<string?>(secret);
        }
        finally
        {
            if (data != IntPtr.Zero)
                Native.SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero)
                Native.CFRelease(item);
        }
    }

    public Task SetAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(key);
        var password = Encoding.UTF8.GetBytes(secret);
        try
        {
            var status = Native.SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                out _, IntPtr.Zero, out var item);
            if (status == ErrSecSuccess && item != IntPtr.Zero)
            {
                try
                {
                    Check(Native.SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password), "update the password in the Keychain");
                }
                finally
                {
                    Native.CFRelease(item);
                }
            }
            else if (status == ErrSecItemNotFound)
            {
                Check(Native.SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                    (uint)password.Length, password, IntPtr.Zero), "save the password to the Keychain");
            }
            else
            {
                Check(status, "access the Keychain");
            }
        }
        finally
        {
            Array.Clear(password);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(key);
        var status = Native.SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
            out _, IntPtr.Zero, out var item);
        if (status == ErrSecItemNotFound)
            return Task.CompletedTask;
        Check(status, "access the Keychain");
        try
        {
            Check(Native.SecKeychainItemDelete(item), "remove the password from the Keychain");
        }
        finally
        {
            Native.CFRelease(item);
        }
        return Task.CompletedTask;
    }

    private static void Check(int status, string action)
    {
        if (status != ErrSecSuccess)
            throw new CredentialVaultException($"Could not {action} (OSStatus {status}).");
    }

    private static class Native
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(Security)]
        public static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
            uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

        [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
        public static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
            uint accountNameLength, byte[] accountName, out uint passwordLength, IntPtr passwordData, out IntPtr itemRef);

        [DllImport(Security)]
        public static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
            uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, IntPtr itemRef);

        [DllImport(Security)]
        public static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

        [DllImport(Security)]
        public static extern int SecKeychainItemDelete(IntPtr itemRef);

        [DllImport(Security)]
        public static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [DllImport(CoreFoundation)]
        public static extern void CFRelease(IntPtr cf);
    }
}
