using System.Runtime.InteropServices;
using System.Text;

namespace GameTuner.Core.Infrastructure;

public interface ICredentialStore
{
    string? Read(string name);
    void Write(string name, string secret);
    void Delete(string name);
}

public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    public string? Read(string name)
    {
        if (!CredRead(name, CredTypeGeneric, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            return credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0
                ? ""
                : Marshal.PtrToStringUni(
                    credential.CredentialBlob,
                    (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Write(string name, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = name,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException(
                    $"无法写入 Windows 凭据管理器，错误码 {Marshal.GetLastWin32Error()}。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string name)
    {
        if (!CredDelete(name, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168)
            {
                throw new InvalidOperationException($"删除 Windows 凭据失败，错误码 {error}。");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint reservedFlag,
        out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(
        [In] ref NativeCredential credential,
        uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(
        string target,
        uint type,
        uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
