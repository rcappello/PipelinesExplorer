using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PipelinesExplorer.VisualStudio.Auth;

/// <summary>
/// Thin wrapper over the Win32 <c>CredRead</c>/<c>CredWrite</c>/<c>CredDelete</c>
/// APIs. Stores the Azure DevOps PAT under a generic credential keyed by a
/// fixed target name, so other tools (and a future <c>vscode</c> extension on
/// the same machine) can co-exist without collisions.
/// </summary>
public sealed class PatCredentialStore
{
    /// <summary>Target name of the credential blob in Windows Credential Manager.</summary>
    public const string TargetName = "PipelinesExplorer.VisualStudio:AzureDevOpsPAT";

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = unchecked((int)0x80070490);

    public string? Read()
    {
        if (!NativeMethods.CredRead(TargetName, CredTypeGeneric, 0, out var credPtr))
        {
            var hr = Marshal.GetHRForLastWin32Error();
            if (hr == ErrorNotFound)
            {
                return null;
            }
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CredRead failed for '{TargetName}'");
        }

        try
        {
            var cred = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(credPtr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
            {
                return string.Empty;
            }

            // PAT was stored as UTF-16 LE.
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            NativeMethods.CredFree(credPtr);
        }
    }

    public void Write(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        var blob = Encoding.Unicode.GetBytes(token);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new NativeMethods.CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = TargetName,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = "PersonalAccessToken",
            };

            if (!NativeMethods.CredWrite(ref cred, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CredWrite failed for '{TargetName}'");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public bool Delete()
    {
        if (NativeMethods.CredDelete(TargetName, CredTypeGeneric, 0))
        {
            return true;
        }

        var hr = Marshal.GetHRForLastWin32Error();
        if (hr == ErrorNotFound)
        {
            return false;
        }
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"CredDelete failed for '{TargetName}'");
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string TargetName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string UserName;
        }

        [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

        [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWrite([In] ref CREDENTIAL credential, uint flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("Advapi32.dll", SetLastError = true)]
        internal static extern void CredFree([In] IntPtr cred);
    }
}
