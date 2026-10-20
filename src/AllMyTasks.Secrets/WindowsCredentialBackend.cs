using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AllMyTasks.Secrets;

/// <summary>Windows Credential Manager (generic credentials, protected by the user's logon).</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialBackend : ISecretBackend
{
    internal const string Prefix = "AllMyTasks/";
    internal const uint CredTypeGeneric = 1;
    internal const uint CredPersistLocalMachine = 2;
    internal const int ErrorNotFound = 1168;

    public string Name => "Windows Credential Manager";

    public Task<string?> GetAsync(string account, CancellationToken cancellationToken)
    {
        if (!CredRead(Prefix + account, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound
                ? Task.FromResult<string?>(null)
                : throw new SecretStoreException($"Credential Manager could not read a secret (error {error}).");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public Task SetAsync(string account, string secret, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var targetName = Marshal.StringToCoTaskMemUni(Prefix + account);
            var userName = Marshal.StringToCoTaskMemUni(Environment.UserName);
            try
            {
                var credential = new Credential
                {
                    Type = CredTypeGeneric,
                    TargetName = targetName,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = CredPersistLocalMachine,
                    UserName = userName,
                };
                if (!CredWrite(ref credential, 0))
                {
                    throw new SecretStoreException($"Credential Manager could not store a secret (error {Marshal.GetLastWin32Error()}).");
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(targetName);
                Marshal.FreeCoTaskMem(userName);
            }
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string account, CancellationToken cancellationToken)
    {
        if (!CredDelete(Prefix + account, CredTypeGeneric, 0) && Marshal.GetLastWin32Error() != ErrorNotFound)
        {
            throw new SecretStoreException($"Credential Manager could not delete a secret (error {Marshal.GetLastWin32Error()}).");
        }

        return Task.CompletedTask;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref Credential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
