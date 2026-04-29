using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using KodaClaw.Contracts.Secrets;

namespace KodaClaw.Workspace.Secrets;

/// <summary>
/// Windows Credential Manager backend via advapi32 P/Invoke.
/// Credentials are visible and manageable in Control Panel → Credential Manager → Windows Credentials.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialManager : IPlatformKeychain
{
    // ERROR_NOT_FOUND: the requested credential was not found in the store.
    private const int ErrorNotFound = 1168;

    public string StorageDisplayName => "Windows Credential Manager";

    public Task<string?> ReadAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);

        if (!CredRead(BuildTargetName(secretRef), CredType.Generic, 0, out var ptr))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ErrorNotFound) return Task.FromResult<string?>(null);
            throw new InvalidOperationException($"Windows Credential Manager read failed: {new Win32Exception(err).Message}");
        }

        try
        {
            var cred = Marshal.PtrToStructure<NativeCredential>(ptr);
            if (cred.CredentialBlobSize == 0) return Task.FromResult<string?>(null);

            var blob = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
            var value = Encoding.Unicode.GetString(blob).Trim('\0');
            return Task.FromResult<string?>(string.IsNullOrEmpty(value) ? null : value);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public Task WriteAsync(SecretRef secretRef, string secretValue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretValue);

        var blob = Encoding.Unicode.GetBytes(secretValue.Trim());
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);

            var cred = new NativeCredential
            {
                Type = CredType.Generic,
                TargetName = BuildTargetName(secretRef),
                Comment = secretRef.DisplayName ?? $"KodaClaw — {secretRef.Scope}/{secretRef.Key}",
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersist.LocalMachine,
                UserName = secretRef.Key,
            };

            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException($"Windows Credential Manager write failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);

        if (!CredDelete(BuildTargetName(secretRef), CredType.Generic, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != ErrorNotFound)
                throw new InvalidOperationException($"Windows Credential Manager delete failed: {new Win32Exception(err).Message}");
        }

        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(SecretRef secretRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretRef);

        if (!CredRead(BuildTargetName(secretRef), CredType.Generic, 0, out var ptr))
            return Task.FromResult(false);

        CredFree(ptr);
        return Task.FromResult(true);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string BuildTargetName(SecretRef secretRef)
        => $"KodaClaw/{secretRef.Scope}/{secretRef.Key}";

    // -------------------------------------------------------------------------
    // P/Invoke
    // -------------------------------------------------------------------------

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        CredType type,
        int reservedFlag,
        out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(
        [In] ref NativeCredential credential,
        uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(
        string target,
        CredType type,
        int reservedFlag);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    // -------------------------------------------------------------------------
    // Native types
    // -------------------------------------------------------------------------

    private enum CredType : uint
    {
        Generic = 1,
    }

    private enum CredPersist : uint
    {
        Session = 1,
        LocalMachine = 2,
        Enterprise = 3,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public CredType Type;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Comment;
        public long LastWritten;       // FILETIME (not used on write)
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CredPersist Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string UserName;
    }
}
