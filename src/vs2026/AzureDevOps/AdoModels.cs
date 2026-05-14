using System.Text.Json.Serialization;

namespace PipelinesExplorer.VisualStudio.AzureDevOps;

/// <summary>Authenticated Azure DevOps profile (a.k.a. <c>profiles/me</c>).</summary>
public sealed class AdoProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>Azure DevOps account (a.k.a. organization).</summary>
public sealed class AdoOrganization
{
    public string AccountId { get; init; } = string.Empty;
    public string AccountName { get; init; } = string.Empty;
    public string AccountUri { get; init; } = string.Empty;
}

public sealed class AdoProject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;
}

public class AdoPipeline
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("folder")]
    public string Folder { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("revision")]
    public int? Revision { get; set; }
}

public sealed class AdoPipelineDetail : AdoPipeline
{
    [JsonPropertyName("configuration")]
    public AdoPipelineConfiguration? Configuration { get; set; }
}

public sealed class AdoPipelineConfiguration
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("repository")]
    public AdoPipelineConfigurationRepository? Repository { get; set; }
}

public sealed class AdoPipelineConfigurationRepository
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("fullName")]
    public string? FullName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class AdoRepository
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("webUrl")]
    public string? WebUrl { get; set; }

    [JsonPropertyName("defaultBranch")]
    public string? DefaultBranch { get; set; }

    [JsonPropertyName("project")]
    public AdoRepositoryProject? Project { get; set; }
}

public sealed class AdoRepositoryProject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

internal sealed class AdoListResponse<T>
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("value")]
    public T[] Value { get; set; } = System.Array.Empty<T>();
}

internal sealed class AccountsResponseEntry
{
    [JsonPropertyName("accountId")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("accountName")]
    public string AccountName { get; set; } = string.Empty;

    [JsonPropertyName("accountUri")]
    public string? AccountUri { get; set; }
}

internal sealed class GitRefEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
