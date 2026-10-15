using System;
using System.Collections.Generic;

namespace PlaywrightMcpHarness.Studio.Models;

public class HarnessConfig
{
    public const string SectionName = "HarnessConfig";

    public string TestsProjectPath { get; set; } = "../PlaywrightMcpHarness.Tests";
    public string ActiveProvider { get; set; } = "LocalOllama";
    public int LlmTimeoutMinutes { get; set; } = 10;
    public Dictionary<string, LlmProviderConfig> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public PlaywrightMcpConfig PlaywrightMcp { get; set; } = new();
}

public class LlmProviderConfig
{
    public string Type { get; set; } = "Ollama"; // "Ollama" or "OpenAI"
    public string Endpoint { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string ApiKey { get; set; } = "";

    public string ResolveApiKey()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            return string.Empty;
        }

        if (ApiKey.StartsWith("ENV:", StringComparison.OrdinalIgnoreCase))
        {
            var envVar = ApiKey.Substring(4).Trim();
            return Environment.GetEnvironmentVariable(envVar) ?? string.Empty;
        }

        return ApiKey;
    }

    public LlmProviderConfig Clone()
    {
        return new LlmProviderConfig
        {
            Type = this.Type,
            Endpoint = this.Endpoint,
            ModelId = this.ModelId,
            ApiKey = this.ApiKey
        };
    }
}

public class PlaywrightMcpConfig
{
    public string Command { get; set; } = "npx";
    public List<string> Arguments { get; set; } = new();
}

public enum VisualExecutionMode
{
    HeadedSlowMo,
    HeadedNormal,
    Headless,
    Inspector
}
