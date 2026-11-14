using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ShinyCommander.Core.Remote;

/// <summary>Passwords and keys, kept out of the settings file.</summary>
public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string secret);
    void Remove(string key);
}

/// <summary>Forgets everything on quit - tests, and machines without a keychain.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    readonly Dictionary<string, string> secrets = new(StringComparer.Ordinal);

    public string? Get(string key)
    {
        lock (this.secrets)
            return this.secrets.GetValueOrDefault(key);
    }

    public void Set(string key, string secret)
    {
        lock (this.secrets)
            this.secrets[key] = secret;
    }

    public void Remove(string key)
    {
        lock (this.secrets)
            this.secrets.Remove(key);
    }
}

/// <summary>
/// The OS keychain: the login Keychain on macOS, Credential Manager on Windows, the Secret Service (GNOME Keyring,
/// KWallet) through secret-tool on Linux. Anything the keychain refuses is held in memory for the session instead,
/// so the user is asked once per run rather than on every connect.
/// </summary>
public sealed class OsSecretStore : ISecretStore
{
    const string Service = "ShinyCommander";
    readonly MemorySecretStore fallback = new();

    public string? Get(string key)
    {
        try
        {
            var value = OperatingSystem.IsMacOS() ? MacKeychain.Get(Service, key)
                : OperatingSystem.IsWindows() ? WindowsCredentials.Get(Service + ":" + key)
                : OperatingSystem.IsLinux() ? LinuxSecretTool.Get(Service, key)
                : null;
            return value ?? this.fallback.Get(key);
        }
        catch
        {
            return this.fallback.Get(key);
        }
    }

    public void Set(string key, string secret)
    {
        this.fallback.Set(key, secret);
        try
        {
            if (OperatingSystem.IsMacOS())
                MacKeychain.Set(Service, key, secret);
            else if (OperatingSystem.IsWindows())
                WindowsCredentials.Set(Service + ":" + key, secret);
            else if (OperatingSystem.IsLinux())
                LinuxSecretTool.Set(Service, key, secret);
        }
        catch
        {
            // no keychain (headless Linux without a Secret Service) - the session copy above still works
        }
    }

    public void Remove(string key)
    {
        this.fallback.Remove(key);
        try
        {
            if (OperatingSystem.IsMacOS())
                MacKeychain.Remove(Service, key);
            else if (OperatingSystem.IsWindows())
                WindowsCredentials.Remove(Service + ":" + key);
            else if (OperatingSystem.IsLinux())
                LinuxSecretTool.Remove(Service, key);
        }
        catch
        {
            // nothing stored, or no keychain
        }
    }

    // the generic password API: deprecated in favour of SecItem*, which needs CFDictionary plumbing, but still supported
    static class MacKeychain
    {
        const string Security = "/System/Library/Frameworks/Security.framework/Security";
        const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        const int ItemNotFound = -25300;

        public static string? Get(string service, string account)
        {
            var s = Encoding.UTF8.GetBytes(service);
            var a = Encoding.UTF8.GetBytes(account);
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, out var length, out var data, out var item);
            if (status == ItemNotFound)
                return null;
            Check(status);

            try
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, (int)length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                CFRelease(item);
            }
        }

        public static void Set(string service, string account, string secret)
        {
            var s = Encoding.UTF8.GetBytes(service);
            var a = Encoding.UTF8.GetBytes(account);
            var p = Encoding.UTF8.GetBytes(secret);
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, out _, out var data, out var item);
            if (status == ItemNotFound)
            {
                Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, (uint)p.Length, p, IntPtr.Zero));
                return;
            }
            Check(status);

            try
            {
                Check(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)p.Length, p));
            }
            finally
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                CFRelease(item);
            }
        }

        public static void Remove(string service, string account)
        {
            var s = Encoding.UTF8.GetBytes(service);
            var a = Encoding.UTF8.GetBytes(account);
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)s.Length, s, (uint)a.Length, a, out _, out var data, out var item);
            if (status == ItemNotFound)
                return;
            Check(status);

            try
            {
                Check(SecKeychainItemDelete(item));
            }
            finally
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                CFRelease(item);
            }
        }

        static void Check(int status)
        {
            if (status != 0)
                throw new InvalidOperationException($"Keychain error {status}");
        }

        [DllImport(Security)]
        static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

        [DllImport(Security)]
        static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, IntPtr itemRef);

        [DllImport(Security)]
        static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

        [DllImport(Security)]
        static extern int SecKeychainItemDelete(IntPtr itemRef);

        [DllImport(Security)]
        static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [DllImport(CoreFoundation)]
        static extern void CFRelease(IntPtr cf);
    }

    static class WindowsCredentials
    {
        const uint GenericCredential = 1;
        const uint PersistLocalMachine = 2;
        const int NotFound = 1168;

        [StructLayout(LayoutKind.Sequential)]
        struct Credential
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

        public static string? Get(string target)
        {
            if (!CredReadW(target, GenericCredential, 0, out var ptr))
            {
                if (Marshal.GetLastWin32Error() == NotFound)
                    return null;
                throw new InvalidOperationException($"Credential Manager error {Marshal.GetLastWin32Error()}");
            }

            try
            {
                var cred = Marshal.PtrToStructure<Credential>(ptr);
                return cred.CredentialBlobSize == 0 ? String.Empty : Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
            }
            finally
            {
                CredFree(ptr);
            }
        }

        public static void Set(string target, string secret)
        {
            var name = Marshal.StringToCoTaskMemUni(target);
            var user = Marshal.StringToCoTaskMemUni(Environment.UserName);
            var blob = Marshal.StringToCoTaskMemUni(secret);
            try
            {
                var cred = new Credential
                {
                    Type = GenericCredential,
                    TargetName = name,
                    UserName = user,
                    CredentialBlob = blob,
                    CredentialBlobSize = (uint)(secret.Length * 2),
                    Persist = PersistLocalMachine
                };
                if (!CredWriteW(ref cred, 0))
                    throw new InvalidOperationException($"Credential Manager error {Marshal.GetLastWin32Error()}");
            }
            finally
            {
                Marshal.FreeCoTaskMem(name);
                Marshal.FreeCoTaskMem(user);
                Marshal.ZeroFreeCoTaskMemUnicode(blob);
            }
        }

        public static void Remove(string target)
        {
            if (!CredDeleteW(target, GenericCredential, 0) && Marshal.GetLastWin32Error() != NotFound)
                throw new InvalidOperationException($"Credential Manager error {Marshal.GetLastWin32Error()}");
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredWriteW(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredDeleteW(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        static extern void CredFree(IntPtr buffer);
    }

    // libsecret's command line tool - the secret goes through stdin, never the command line
    static class LinuxSecretTool
    {
        public static string? Get(string service, string account)
        {
            var (code, output) = Run(null, "lookup", "service", service, "account", account);
            return code == 0 ? output : null;
        }

        public static void Set(string service, string account, string secret)
        {
            var (code, _) = Run(secret, "store", "--label", $"{service} ({account})", "service", service, "account", account);
            if (code != 0)
                throw new InvalidOperationException("secret-tool store failed");
        }

        public static void Remove(string service, string account) => Run(null, "clear", "service", service, "account", account);

        static (int Code, string Output) Run(string? input, params string[] args)
        {
            var info = new ProcessStartInfo("secret-tool")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in args)
                info.ArgumentList.Add(arg);

            using var process = Process.Start(info) ?? throw new InvalidOperationException("secret-tool not found");
            if (input != null)
                process.StandardInput.Write(input);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                return (-1, String.Empty);
            }
            return (process.ExitCode, output);
        }
    }
}
