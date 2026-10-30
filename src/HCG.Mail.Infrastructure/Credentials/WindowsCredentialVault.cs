using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using HCG.Mail.Core.Security;

namespace HCG.Mail.Infrastructure.Credentials;

/// <summary>
/// Windows Credential Manager (generic credentials). Secrets are protected by Windows for the
/// signed-in user and persisted locally (not roamed). Visible in Control Panel → Credential
/// Manager → Windows Credentials as "HCG Mail/…".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialVault : ICredentialVault
{
    private const string TargetPrefix = "HCG Mail/";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaxBlobBytes = 5 * 512;

    public bool IsPersistent => true;

    public string StorageDescription => "Windows Credential Manager";

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!NativeMethods.CredRead(TargetPrefix + key, CredTypeGeneric, 0, out var ptr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
                return Task.FromResult<string?>(null);
            throw new CredentialVaultException("Could not read the password from Windows Credential Manager.", new Win32Exception(error));
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(ptr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return Task.FromResult<string?>("");
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var secret = Encoding.UTF8.GetString(bytes);
            Array.Clear(bytes);
            return Task.FromResult<string?>(secret);
        }
        finally
        {
            NativeMethods.CredFree(ptr);
        }
    }

    public Task SetAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length > MaxBlobBytes)
            throw new CredentialVaultException("The password is too long to store in Windows Credential Manager.");

        var blob = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeMethods.Credential
            {
                Type = CredTypeGeneric,
                TargetName = TargetPrefix + key,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = key,
                Comment = "HCG Mail account password",
            };
            if (!NativeMethods.CredWrite(ref credential, 0))
                throw new CredentialVaultException("Could not save the password to Windows Credential Manager.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally
        {
            // Overwrite the unmanaged copy before freeing it.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!NativeMethods.CredDelete(TargetPrefix + key, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
                throw new CredentialVaultException("Could not remove the password from Windows Credential Manager.", new Win32Exception(error));
        }
        return Task.CompletedTask;
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredWrite([In] ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll", SetLastError = false)]
        public static extern void CredFree(IntPtr buffer);
    }
}
