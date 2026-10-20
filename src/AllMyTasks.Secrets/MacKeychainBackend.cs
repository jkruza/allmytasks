using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AllMyTasks.Secrets;

/// <summary>macOS Keychain through the Security framework's generic-password functions.</summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacKeychainBackend : ISecretBackend
{
    internal const string Service = "AllMyTasks";
    internal const string Security = "/System/Library/Frameworks/Security.framework/Security";
    internal const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    internal const int ErrSecItemNotFound = -25300;

    public string Name => "macOS Keychain";

    public Task<string?> GetAsync(string account, CancellationToken cancellationToken)
    {
        var status = Find(account, out var data, out var length, out var item);
        if (status == ErrSecItemNotFound)
        {
            return Task.FromResult<string?>(null);
        }

        Check(status, "read");
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero)
            {
                CFRelease(item);
            }
        }
    }

    public Task SetAsync(string account, string secret, CancellationToken cancellationToken)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var name = Encoding.UTF8.GetBytes(account);
        var password = Encoding.UTF8.GetBytes(secret);
        try
        {
            var status = Find(account, out var existingData, out _, out var existing);
            if (status == 0)
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, existingData);
                try
                {
                    Check(SecKeychainItemModifyAttributesAndData(existing, IntPtr.Zero, (uint)password.Length, password), "update");
                }
                finally
                {
                    CFRelease(existing);
                }
            }
            else if (status == ErrSecItemNotFound)
            {
                Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)name.Length, name, (uint)password.Length, password, out var created), "store");
                if (created != IntPtr.Zero)
                {
                    CFRelease(created);
                }
            }
            else
            {
                Check(status, "store");
            }
        }
        finally
        {
            Array.Clear(password);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string account, CancellationToken cancellationToken)
    {
        var status = Find(account, out var data, out _, out var item);
        if (status == ErrSecItemNotFound)
        {
            return Task.CompletedTask;
        }

        Check(status, "delete");
        try
        {
            _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            Check(SecKeychainItemDelete(item), "delete");
        }
        finally
        {
            CFRelease(item);
        }

        return Task.CompletedTask;
    }

    private static int Find(string account, out IntPtr data, out uint length, out IntPtr item)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var name = Encoding.UTF8.GetBytes(account);
        return SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)name.Length, name, out length, out data, out item);
    }

    private static void Check(int status, string action)
    {
        if (status != 0)
        {
            throw new SecretStoreException($"The macOS Keychain could not {action} a secret (status {status}).");
        }
    }

    [LibraryImport(Security)]
    private static partial int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        uint passwordLength,
        byte[] passwordData,
        out IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemDelete(IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr cf);
}
