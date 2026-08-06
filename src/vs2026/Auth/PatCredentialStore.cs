using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using PipelinesExplorer.VisualStudio.Services;

namespace PipelinesExplorer.VisualStudio.Auth;

/// <summary>
/// Storage abstraction for Azure DevOps PATs used by the PAT sign-in flow.
///
/// Two slot kinds are supported:
/// <list type="bullet">
///   <item>A <b>global</b> slot backed by the Windows Credential Manager target
///     <see cref="TargetName"/>. Holds an <i>All accessible organizations</i>
///     PAT — the historical behavior. Still readable/writable after
///     1 Dec 2026 but expected to become empty over time.</item>
///   <item><b>Per-organization</b> slots backed by
///     <see cref="PerOrgTargetPrefix"/><c>{canonical-org}</c> targets. One PAT
///     per Azure DevOps organization. Introduced by plan
///     <c>002-pat-per-org-fallback</c> because
///     <c>app.vssps.visualstudio.com/_apis/accounts</c> is not a deterministic
///     enumerator across tenants (see §1.1 of that plan).</item>
/// </list>
/// The set of known per-org slots is tracked in <see cref="JsonStateStore"/>
/// under <see cref="IndexKey"/>. Callers must go through this class rather
/// than talking to Credential Manager directly to keep the index consistent.
/// </summary>
public sealed class PatCredentialStore
{
    /// <summary>Target name of the global credential blob in Windows Credential Manager.</summary>
    public const string TargetName = "PipelinesExplorer.VisualStudio:AzureDevOpsPAT";

    /// <summary>Prefix for per-organization credential targets: <c>{prefix}{canonical-org}</c>.</summary>
    public const string PerOrgTargetPrefix = "PipelinesExplorer.VisualStudio:AzureDevOpsPAT/";

    /// <summary>Key under which the list of known per-org names is persisted.</summary>
    public const string IndexKey = "pipelinesexplorer.pat.org.index";

    /// <summary>
    /// Rolling list of canonical org names the user has ever added via the
    /// per-organization flow. Survives <c>SignOut</c> (which clears the PAT
    /// slots) but is wiped by <see cref="ClearAll"/> alongside every other
    /// stored artefact.
    /// </summary>
    public const string HistoryKey = "pipelinesexplorer.pat.org.history";

    /// <summary>Cap on the number of entries retained by <see cref="GetHistory"/>.</summary>
    public const int HistoryLimit = 20;

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = unchecked((int)0x80070490);

    private readonly JsonStateStore _state;

    public PatCredentialStore(JsonStateStore? state = null)
    {
        _state = state ?? JsonStateStore.Shared;
    }

    // ---------- global slot (unchanged API) ----------

    public string? Read() => ReadCredential(TargetName);

    public void Write(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        WriteCredential(TargetName, token, userName: "PersonalAccessToken");
    }

    public bool Delete() => DeleteCredential(TargetName);

    // ---------- per-org slots ----------

    /// <summary>
    /// Persist <paramref name="pat"/> for <paramref name="org"/>. Overwrites
    /// any previous value for the same (canonicalized) org name, updates the
    /// per-org index and records the org in the history so future prompts
    /// can suggest it.
    /// </summary>
    public void SavePerOrgPat(string org, string pat)
    {
        ArgumentException.ThrowIfNullOrEmpty(org);
        ArgumentException.ThrowIfNullOrEmpty(pat);
        var canonical = CanonicalizeOrg(org);
        WriteCredential(PerOrgTargetPrefix + canonical, pat, userName: "PersonalAccessToken:" + canonical);
        var index = ReadIndex();
        if (!index.Contains(canonical, StringComparer.OrdinalIgnoreCase))
        {
            index.Add(canonical);
            index.Sort(StringComparer.OrdinalIgnoreCase);
            _state.Set(IndexKey, index);
        }
        RememberInHistory(canonical);
    }

    public string? ReadPerOrgPat(string org)
    {
        ArgumentException.ThrowIfNullOrEmpty(org);
        return ReadCredential(PerOrgTargetPrefix + CanonicalizeOrg(org));
    }

    public void DeletePerOrgPat(string org)
    {
        ArgumentException.ThrowIfNullOrEmpty(org);
        var canonical = CanonicalizeOrg(org);
        DeleteCredential(PerOrgTargetPrefix + canonical);
        var index = ReadIndex();
        var removed = index.RemoveAll(o => string.Equals(o, canonical, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            if (index.Count > 0)
            {
                _state.Set(IndexKey, index);
            }
            else
            {
                _state.Remove(IndexKey);
            }
        }
    }

    /// <summary>
    /// Return every known per-org PAT. Index entries whose credential is
    /// missing (e.g. cleared out-of-band via Credential Manager) are dropped
    /// on the fly to keep the index consistent.
    /// </summary>
    public IReadOnlyList<PerOrgPat> ListPerOrgPats()
    {
        var index = ReadIndex();
        var result = new List<PerOrgPat>(index.Count);
        var survivors = new List<string>(index.Count);
        foreach (var org in index)
        {
            var pat = ReadCredential(PerOrgTargetPrefix + org);
            if (!string.IsNullOrEmpty(pat))
            {
                result.Add(new PerOrgPat(org, pat!));
                survivors.Add(org);
            }
        }
        if (survivors.Count != index.Count)
        {
            if (survivors.Count > 0)
            {
                _state.Set(IndexKey, survivors);
            }
            else
            {
                _state.Remove(IndexKey);
            }
        }
        return result;
    }

    /// <summary>Return the canonical org names that currently have a stored PAT.</summary>
    public IReadOnlyList<string> ListPerOrgNames() => ReadIndex();

    public void ClearAllPerOrgPats()
    {
        foreach (var org in ReadIndex())
        {
            try
            {
                DeleteCredential(PerOrgTargetPrefix + org);
            }
            catch
            {
                // best-effort; if one entry fails we still want to clear the index
            }
        }
        _state.Remove(IndexKey);
    }

    // ---------- cross-slot ----------

    /// <summary>Wipe the global slot, every per-org slot and the history.</summary>
    public void ClearAll()
    {
        Delete();
        ClearAllPerOrgPats();
        _state.Remove(HistoryKey);
    }

    // ---------- history (survives sign-out) ----------

    /// <summary>
    /// Return org names the user has previously added via the per-org flow,
    /// most-recent first. The list survives <c>SignOut</c> — only
    /// <see cref="ClearAll"/> (backing the <c>Reset</c> command) wipes it.
    /// </summary>
    public IReadOnlyList<string> GetHistory()
    {
        var raw = _state.Get<List<string>>(HistoryKey, new List<string>()) ?? new List<string>();
        return raw.Where(s => !string.IsNullOrEmpty(s)).ToList();
    }

    private void RememberInHistory(string canonicalOrg)
    {
        var current = new List<string>(GetHistory());
        current.RemoveAll(o => string.Equals(o, canonicalOrg, StringComparison.OrdinalIgnoreCase));
        current.Insert(0, canonicalOrg);
        if (current.Count > HistoryLimit)
        {
            current = current.Take(HistoryLimit).ToList();
        }
        _state.Set(HistoryKey, current);
    }

    private List<string> ReadIndex()
    {
        var raw = _state.Get<List<string>>(IndexKey, new List<string>()) ?? new List<string>();
        return raw.Where(s => !string.IsNullOrEmpty(s)).ToList();
    }

    /// <summary>
    /// Canonical form for an Azure DevOps organization name: trimmed and
    /// lowercased (invariant culture). Matches how <c>dev.azure.com</c>
    /// treats the org portion of a URL and gives us a stable key for both
    /// storage and de-duplication.
    /// </summary>
    public static string CanonicalizeOrg(string org)
    {
        ArgumentNullException.ThrowIfNull(org);
        return org.Trim().ToLowerInvariant();
    }

    // ---------- credential-manager plumbing ----------

    private static string? ReadCredential(string target)
    {
        if (!NativeMethods.CredRead(target, CredTypeGeneric, 0, out var credPtr))
        {
            var hr = Marshal.GetHRForLastWin32Error();
            if (hr == ErrorNotFound)
            {
                return null;
            }
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CredRead failed for '{target}'");
        }

        try
        {
            var cred = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(credPtr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
            {
                return string.Empty;
            }

            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            NativeMethods.CredFree(credPtr);
        }
    }

    private static void WriteCredential(string target, string token, string userName)
    {
        var blob = Encoding.Unicode.GetBytes(token);
        var blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new NativeMethods.CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = userName,
            };

            if (!NativeMethods.CredWrite(ref cred, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CredWrite failed for '{target}'");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    private static bool DeleteCredential(string target)
    {
        if (NativeMethods.CredDelete(target, CredTypeGeneric, 0))
        {
            return true;
        }

        var hr = Marshal.GetHRForLastWin32Error();
        if (hr == ErrorNotFound)
        {
            return false;
        }
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"CredDelete failed for '{target}'");
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

/// <summary>A per-organization PAT entry as returned by <see cref="PatCredentialStore.ListPerOrgPats"/>.</summary>
public sealed record PerOrgPat(string Org, string Pat);
