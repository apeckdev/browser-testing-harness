using System;
using System.Collections.Generic;
using System.IO;

namespace PlaywrightMcpHarness.Studio.Models;

public class HarnessConfig
{
    public const string SectionName = "HarnessConfig";

    public string TestsProjectPath { get; set; } = "../PlaywrightMcpHarness.Tests";
    public string ActiveProvider { get; set; } = "LocalOllama";
    public int LlmTimeoutMinutes { get; set; } = 10;
    public bool UsePageObjectModel { get; set; } = false;
    public double VisualTolerancePercent { get; set; } = 0.2;
    public Dictionary<string, LlmProviderConfig> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public PlaywrightMcpConfig PlaywrightMcp { get; set; } = new();

    public string ResolveTestsProjectRoot()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, TestsProjectPath));
        if (!Directory.Exists(path) && Directory.Exists(Path.GetFullPath(TestsProjectPath)))
        {
            path = Path.GetFullPath(TestsProjectPath);
        }
        return path;
    }

    public string ResolveSafePath(string relativePath, string? subDirectory = null)
    {
        var root = Path.GetFullPath(ResolveTestsProjectRoot());
        var baseDir = string.IsNullOrWhiteSpace(subDirectory)
            ? root
            : Path.GetFullPath(Path.Combine(root, subDirectory));

        var combined = Path.GetFullPath(Path.Combine(baseDir, relativePath));
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!combined.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Path '{relativePath}' resolves outside the test project root.");
        }

        return combined;
    }
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
    public string? ServerUrl { get; set; }
    public string Command { get; set; } = "npx";
    public List<string> Arguments { get; set; } = new();

    public bool IsRemote => !string.IsNullOrWhiteSpace(ServerUrl);

    public PlaywrightMcpConfig Clone()
    {
        return new PlaywrightMcpConfig
        {
            ServerUrl = this.ServerUrl,
            Command = this.Command,
            Arguments = new List<string>(this.Arguments)
        };
    }
}

public enum VisualExecutionMode
{
    HeadedSlowMo,
    HeadedNormal,
    Headless,
    Inspector
}
