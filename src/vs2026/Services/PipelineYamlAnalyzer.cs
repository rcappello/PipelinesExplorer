using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PipelinesExplorer.VisualStudio.AzureDevOps;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace PipelinesExplorer.VisualStudio.Services;

/// <summary>Reference to a YAML template (`template:` key).</summary>
public sealed class TemplateRef
{
    /// <summary>Raw <c>template:</c> value as written in YAML.</summary>
    public string Raw { get; }
    /// <summary>Local path portion (no <c>@repo</c> suffix).</summary>
    public string Path { get; }
    /// <summary>Optional repository alias if the template lives in another repo.</summary>
    public string? Repository { get; }

    public TemplateRef(string raw, string path, string? repository)
    {
        Raw = raw;
        Path = path;
        Repository = repository;
    }
}

/// <summary>Reference to a PowerShell-style task in a pipeline.</summary>
public sealed class PowerShellRef
{
    public string Task { get; }
    public string? FilePath { get; }
    public bool Inline { get; }
    public int? Line { get; }

    public PowerShellRef(string task, string? filePath, bool inline, int? line)
    {
        Task = task;
        FilePath = filePath;
        Inline = inline;
        Line = line;
    }
}

/// <summary>Result of analysing a single YAML file.</summary>
public sealed class PipelineAnalysis
{
    public IReadOnlyList<TemplateRef> Templates { get; init; } = Array.Empty<TemplateRef>();
    public IReadOnlyList<PowerShellRef> Scripts { get; init; } = Array.Empty<PowerShellRef>();
    public string? RootPath { get; init; }
    public string? Warning { get; init; }
}

/// <summary>
/// Loads YAML for pipelines or individual templates and walks the AST to
/// extract referenced templates and PowerShell-style tasks. Mirrors the
/// VS Code client's <c>PipelineYamlAnalyzer</c>.
/// </summary>
public sealed partial class PipelineYamlAnalyzer
{
    [GeneratedRegex(@"^(PowerShell|AzurePowerShell|Powershell|AzureCLI)@\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex PowerShellTaskRe();

    private readonly AdoClient _client;
    private readonly LoggingService _logger;

    public PipelineYamlAnalyzer(AdoClient client, LoggingService logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>Analyse an arbitrary YAML file inside a TfsGit repository.</summary>
    public async Task<PipelineAnalysis> AnalyzeFileAsync(
        string organizationName,
        string projectName,
        string repositoryId,
        string filePath,
        string? branch = null,
        CancellationToken cancellationToken = default)
    {
        string? yamlText;
        try
        {
            yamlText = await _client.GetFileContentAsync(organizationName, projectName, repositoryId, filePath, branch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to fetch YAML {filePath}", ex);
            return new PipelineAnalysis { RootPath = filePath, Warning = "Could not fetch YAML file." };
        }
        if (string.IsNullOrEmpty(yamlText))
        {
            return new PipelineAnalysis { RootPath = filePath, Warning = "YAML file not found in the repository." };
        }

        try
        {
            var templates = new List<TemplateRef>();
            var scripts = new List<PowerShellRef>();
            using var reader = new System.IO.StringReader(yamlText!);
            var stream = new YamlStream();
            stream.Load(reader);
            foreach (var doc in stream.Documents)
            {
                Walk(doc.RootNode, templates, scripts);
            }
            return new PipelineAnalysis
            {
                Templates = DedupeTemplates(templates),
                Scripts = DedupeScripts(scripts),
                RootPath = filePath,
            };
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to parse YAML {filePath}", ex);
            return new PipelineAnalysis { RootPath = filePath, Warning = "YAML parse error." };
        }
    }

    /// <summary>Analyse a pipeline by id, fetching its definition first if needed.</summary>
    public async Task<PipelineAnalysis> AnalyzeAsync(
        string organizationName,
        string projectName,
        int pipelineId,
        AdoPipelineDetail? preloadedDetail = null,
        string? branch = null,
        CancellationToken cancellationToken = default)
    {
        AdoPipelineDetail detail;
        if (preloadedDetail is not null)
        {
            detail = preloadedDetail;
        }
        else
        {
            try
            {
                detail = await _client.GetPipelineAsync(organizationName, projectName, pipelineId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to fetch pipeline {pipelineId} definition", ex);
                return new PipelineAnalysis { Warning = "Could not fetch pipeline definition." };
            }
        }

        var cfg = detail.Configuration;
        if (cfg is null || (!string.IsNullOrEmpty(cfg.Type) && !string.Equals(cfg.Type, "yaml", StringComparison.OrdinalIgnoreCase)))
        {
            return new PipelineAnalysis { Warning = "Pipeline is not YAML-based." };
        }
        var repoId = cfg.Repository?.Id;
        var yamlPath = cfg.Path;
        if (string.IsNullOrEmpty(repoId) || string.IsNullOrEmpty(yamlPath))
        {
            return new PipelineAnalysis { Warning = "Pipeline definition has no repository / path." };
        }
        if (!string.IsNullOrEmpty(cfg.Repository?.Type)
            && !string.Equals(cfg.Repository!.Type, "azureReposGit", StringComparison.OrdinalIgnoreCase))
        {
            return new PipelineAnalysis
            {
                RootPath = yamlPath,
                Warning = $"Repository type \"{cfg.Repository.Type}\" is not supported yet.",
            };
        }

        return await AnalyzeFileAsync(organizationName, projectName, repoId!, yamlPath!, branch, cancellationToken).ConfigureAwait(false);
    }

    private static void Walk(YamlNode node, List<TemplateRef> templates, List<PowerShellRef> scripts)
    {
        switch (node)
        {
            case YamlMappingNode map:
                {
                    if (TryGetScalar(map, "template", out var tplVal))
                    {
                        templates.Add(ParseTemplateRef(tplVal!));
                    }
                    if (TryGetScalar(map, "task", out var taskVal) && PowerShellTaskRe().IsMatch(taskVal!))
                    {
                        YamlNode? inputsNode = null;
                        foreach (var kv in map.Children)
                        {
                            if (kv.Key is YamlScalarNode s && string.Equals(s.Value, "inputs", StringComparison.Ordinal))
                            {
                                inputsNode = kv.Value;
                                break;
                            }
                        }
                        var line = map.Start.Line > 0 ? (int?)map.Start.Line : null;
                        scripts.Add(ParseTaskRef(taskVal!, inputsNode, line));
                    }
                    foreach (var kv in map.Children)
                    {
                        Walk(kv.Value, templates, scripts);
                    }
                    break;
                }
            case YamlSequenceNode seq:
                foreach (var child in seq.Children) { Walk(child, templates, scripts); }
                break;
        }
    }

    private static bool TryGetScalar(YamlMappingNode map, string key, out string? value)
    {
        foreach (var kv in map.Children)
        {
            if (kv.Key is YamlScalarNode s && string.Equals(s.Value, key, StringComparison.Ordinal)
                && kv.Value is YamlScalarNode v)
            {
                value = v.Value;
                return value is not null;
            }
        }
        value = null;
        return false;
    }

    private static TemplateRef ParseTemplateRef(string raw)
    {
        var at = raw.LastIndexOf('@');
        if (at < 0) { return new TemplateRef(raw, raw, null); }
        return new TemplateRef(raw, raw.Substring(0, at), raw.Substring(at + 1));
    }

    private static PowerShellRef ParseTaskRef(string task, YamlNode? inputs, int? line)
    {
        if (inputs is not YamlMappingNode inputsMap)
        {
            return new PowerShellRef(task, null, true, line);
        }
        var lower = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in inputsMap.Children)
        {
            if (kv.Key is YamlScalarNode k && k.Value is not null)
            {
                lower[k.Value] = (kv.Value as YamlScalarNode)?.Value;
            }
        }
        lower.TryGetValue("targetType", out var t1);
        lower.TryGetValue("scriptType", out var t2);
        lower.TryGetValue("scriptLocation", out var t3);
        var targetType = (t1 ?? t2 ?? t3 ?? string.Empty).ToLowerInvariant();
        lower.TryGetValue("filePath", out var fp1);
        lower.TryGetValue("scriptPath", out var fp2);
        var filePath = !string.IsNullOrEmpty(fp1) ? fp1 : (!string.IsNullOrEmpty(fp2) ? fp2 : null);
        if (!string.IsNullOrEmpty(filePath))
        {
            return new PowerShellRef(task, filePath, false, line);
        }
        var isInline =
            targetType == "inline" || targetType == "inlinescript"
            || lower.ContainsKey("script") || lower.ContainsKey("inline") || lower.ContainsKey("inlineScript");
        return new PowerShellRef(task, null, isInline, line);
    }

    private static IReadOnlyList<TemplateRef> DedupeTemplates(IEnumerable<TemplateRef> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return items.Where(t => seen.Add(t.Raw)).ToList();
    }

    private static IReadOnlyList<PowerShellRef> DedupeScripts(IEnumerable<PowerShellRef> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return items.Where(s =>
        {
            var key = $"{s.Task}|{s.FilePath ?? (s.Inline ? $"<inline:{s.Line?.ToString() ?? "?"}>" : "<unknown>")}";
            return seen.Add(key);
        }).ToList();
    }
}
