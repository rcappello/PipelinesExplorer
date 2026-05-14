using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PipelinesExplorer.VisualStudio.Services;

/// <summary>
/// Per-repository branch override. When set, the extension reads YAML
/// (pipeline + templates + scripts) from the chosen branch instead of the
/// repository's default branch on Azure DevOps. Mirrors
/// <c>RepoBranchService</c> in the VS Code client (storage key:
/// <c>pipelinesexplorer.repoBranches.v1</c>).
/// </summary>
public sealed class RepoBranchService
{
    private const string StorageKey = "pipelinesexplorer.repoBranches.v1";

    private readonly LoggingService _logger;
    private readonly JsonStateStore _store;

    public RepoBranchService(LoggingService logger, JsonStateStore? store = null)
    {
        _logger = logger;
        _store = store ?? JsonStateStore.Shared;

        var all = ReadAll();
        if (all.Count > 0)
        {
            _logger.Info(
                $"RepoBranchService: loaded {all.Count} branch override(s) from state: {JsonSerializer.Serialize(all)}");
        }
        else
        {
            _logger.Info("RepoBranchService: no branch overrides stored");
        }
    }

    public event EventHandler? Changed;

    public string? Get(RepoLinkKey key) =>
        ReadAll().TryGetValue(key.Encode(), out var v) ? v : null;

    public void Set(RepoLinkKey key, string branch)
    {
        var all = ReadAll();
        all[key.Encode()] = branch;
        WriteAll(all);
        _logger.Info($"Branch override {key.Encode()} -> {branch}");
    }

    public void Clear(RepoLinkKey key)
    {
        var all = ReadAll();
        if (all.Remove(key.Encode()))
        {
            WriteAll(all);
            _logger.Info($"Branch override cleared for {key.Encode()}");
        }
    }

    private Dictionary<string, string> ReadAll() =>
        _store.Get(StorageKey, new Dictionary<string, string>(StringComparer.Ordinal));

    private void WriteAll(Dictionary<string, string> value)
    {
        _store.Set(StorageKey, value);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
