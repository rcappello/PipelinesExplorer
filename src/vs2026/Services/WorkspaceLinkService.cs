using System;
using System.Collections.Generic;

namespace PipelinesExplorer.VisualStudio.Services;

/// <summary>
/// Persists a mapping from <see cref="RepoLinkKey"/> to a local folder path.
/// Used to resolve template/script references to files on disk when the user
/// activates a tree item. Mirrors <c>WorkspaceLinkService</c> in the VS Code
/// client (storage key: <c>pipelinesexplorer.repoLinks.v1</c>).
/// </summary>
public sealed class WorkspaceLinkService
{
    private const string StorageKey = "pipelinesexplorer.repoLinks.v1";

    private readonly LoggingService _logger;
    private readonly JsonStateStore _store;

    public WorkspaceLinkService(LoggingService logger, JsonStateStore? store = null)
    {
        _logger = logger;
        _store = store ?? JsonStateStore.Shared;
    }

    /// <summary>Raised whenever the underlying mapping is mutated.</summary>
    public event EventHandler? Changed;

    public string? Get(RepoLinkKey key)
    {
        return ReadAll().TryGetValue(key.Encode(), out var v) ? v : null;
    }

    public void Set(RepoLinkKey key, string fsPath)
    {
        var all = ReadAll();
        all[key.Encode()] = fsPath;
        WriteAll(all);
        _logger.Info($"Linked {key.Encode()} -> {fsPath}");
    }

    public void Remove(RepoLinkKey key)
    {
        var all = ReadAll();
        if (all.Remove(key.Encode()))
        {
            WriteAll(all);
            _logger.Info($"Unlinked {key.Encode()}");
        }
    }

    /// <summary>
    /// Look up by <c>repoKey</c> alone — useful when a template references a
    /// repository we have not seen as a tree node (cross-project resource ref).
    /// </summary>
    public string? FindAnyByRepoKey(string repoKey)
    {
        var suffix = "::" + repoKey;
        foreach (var kv in ReadAll())
        {
            if (kv.Key.EndsWith(suffix, StringComparison.Ordinal))
            {
                return kv.Value;
            }
        }
        return null;
    }

    private Dictionary<string, string> ReadAll() =>
        _store.Get(StorageKey, new Dictionary<string, string>(StringComparer.Ordinal));

    private void WriteAll(Dictionary<string, string> value)
    {
        _store.Set(StorageKey, value);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
