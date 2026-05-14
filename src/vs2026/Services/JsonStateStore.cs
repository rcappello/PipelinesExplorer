using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace PipelinesExplorer.VisualStudio.Services;

/// <summary>
/// Tiny key/value JSON store backed by a single file under
/// <c>%LocalAppData%\PipelinesExplorer\state.json</c>. Used by services that
/// need cross-session persistence (workspace links, branch overrides) and
/// stand in for VS Code's <c>ExtensionContext.globalState</c>.
/// </summary>
public sealed class JsonStateStore
{
    private static readonly Lazy<JsonStateStore> _shared = new(() => new JsonStateStore());

    private readonly string _filePath;
    private readonly System.Threading.Lock _gate = new();
    private readonly ConcurrentDictionary<string, JsonElement> _values;
    private readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

    public static JsonStateStore Shared => _shared.Value;

    private JsonStateStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PipelinesExplorer");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "state.json");
        _values = Load(_filePath);
    }

    /// <summary>Path of the JSON file backing this store.</summary>
    public string FilePath => _filePath;

    /// <summary>Returns the value for <paramref name="key"/> or <paramref name="defaultValue"/> if missing.</summary>
    public T Get<T>(string key, T defaultValue)
    {
        if (!_values.TryGetValue(key, out var element))
        {
            return defaultValue;
        }

        try
        {
            return element.Deserialize<T>() ?? defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/> and persists to disk.</summary>
    public void Set<T>(string key, T value)
    {
        var element = JsonSerializer.SerializeToElement(value);
        _values[key] = element;
        Persist();
    }

    /// <summary>Removes <paramref name="key"/>; returns <c>true</c> if it existed.</summary>
    public bool Remove(string key)
    {
        if (!_values.TryRemove(key, out _))
        {
            return false;
        }
        Persist();
        return true;
    }

    private void Persist()
    {
        // Snapshot to a plain dictionary so the on-disk shape is deterministic.
        var snapshot = new Dictionary<string, JsonElement>(_values);
        lock (_gate)
        {
            try
            {
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, _writeOptions));
                if (File.Exists(_filePath))
                {
                    File.Replace(tmp, _filePath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tmp, _filePath);
                }
            }
            catch
            {
                // Persistence failures are non-fatal at runtime; swallow so the UI keeps working.
            }
        }
    }

    private static ConcurrentDictionary<string, JsonElement> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new ConcurrentDictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new ConcurrentDictionary<string, JsonElement>(StringComparer.Ordinal);
            }
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                ?? new Dictionary<string, JsonElement>();
            return new ConcurrentDictionary<string, JsonElement>(dict, StringComparer.Ordinal);
        }
        catch
        {
            return new ConcurrentDictionary<string, JsonElement>(StringComparer.Ordinal);
        }
    }
}
