using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlaywrightMcpHarness.Studio.Models;

namespace PlaywrightMcpHarness.Studio.Services;

public interface IStepCatalogService
{
    IReadOnlyList<StepDefinitionInfo> Steps { get; }
    event Action? OnCatalogUpdated;

    Task<List<StepDefinitionInfo>> ScanStepDefinitionsAsync(bool forceReload = false);
    string GetCatalogSummaryForPrompt();
    List<StepDefinitionInfo> SearchSteps(string query, string? stepType = null);
}

public class StepCatalogService : IStepCatalogService
{
    private readonly HarnessConfig _config;
    private readonly ILogger<StepCatalogService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<StepDefinitionInfo> _cachedSteps = new();
    private bool _hasLoaded = false;

    public event Action? OnCatalogUpdated;
    public IReadOnlyList<StepDefinitionInfo> Steps => _cachedSteps;

    public StepCatalogService(
        IOptions<HarnessConfig> configOptions,
        ILogger<StepCatalogService> logger)
    {
        _config = configOptions.Value;
        _logger = logger;
    }

    public async Task<List<StepDefinitionInfo>> ScanStepDefinitionsAsync(bool forceReload = false)
    {
        if (_hasLoaded && !forceReload)
        {
            return _cachedSteps;
        }

        await _lock.WaitAsync();
        try
        {
            if (_hasLoaded && !forceReload)
            {
                return _cachedSteps;
            }

            var stepsDir = GetStepDefinitionsDirectory();
            if (!Directory.Exists(stepsDir))
            {
                _logger.LogWarning("StepDefinitions directory not found at: {StepsDir}", stepsDir);
                _cachedSteps = new();
                _hasLoaded = true;
                return _cachedSteps;
            }

            var parsedSteps = new List<StepDefinitionInfo>();
            var csFiles = Directory.GetFiles(stepsDir, "*.cs", SearchOption.AllDirectories);

            foreach (var filePath in csFiles)
            {
                var fileName = Path.GetFileName(filePath);
                if (fileName.StartsWith("TemporaryGeneratedFile", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var fileContent = await File.ReadAllTextAsync(filePath);
                    var fileSteps = ParseStepDefinitionsFromSource(fileContent, fileName);
                    parsedSteps.AddRange(fileSteps);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse step definitions from file {FileName}", fileName);
                }
            }

            // Sort: Given, When, Then, then alphabetical
            _cachedSteps = parsedSteps
                .OrderBy(s => GetStepTypeOrder(s.StepType))
                .ThenBy(s => s.NormalizedPattern)
                .ToList();

            _hasLoaded = true;
            _logger.LogInformation("Indexed {Count} step definitions across {FileCount} files.", _cachedSteps.Count, csFiles.Length);
            OnCatalogUpdated?.Invoke();

            return _cachedSteps;
        }
        finally
        {
            _lock.Release();
        }
    }

    public List<StepDefinitionInfo> SearchSteps(string query, string? stepType = null)
    {
        var steps = _cachedSteps.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(stepType) && !stepType.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            steps = steps.Where(s => s.StepType.Equals(stepType, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            steps = steps.Where(s =>
                s.NormalizedPattern.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.MethodName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.SourceFile.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        return steps.ToList();
    }

    public string GetCatalogSummaryForPrompt()
    {
        if (_cachedSteps.Count == 0)
        {
            return "No existing step definitions found in project.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("### INDEXED SPECFLOW STEP DEFINITIONS LIBRARY (REUSE THESE FIRST):");
        sb.AppendLine("Always reuse these existing steps in your .feature scenarios whenever possible.");
        sb.AppendLine("Only write new C# step definitions when an action or assertion is not covered below.\n");

        var grouped = _cachedSteps.GroupBy(s => s.StepType);
        foreach (var group in grouped.OrderBy(g => GetStepTypeOrder(g.Key)))
        {
            sb.AppendLine($"#### {group.Key.ToUpperInvariant()} Steps:");
            foreach (var step in group)
            {
                var paramDesc = step.Parameters.Count > 0
                    ? $" (Parameters: {string.Join(", ", step.Parameters.Select(p => $"{p.Type} {p.Name}"))})"
                    : "";
                sb.AppendLine($"- {step.StepType} {step.NormalizedPattern} [File: {step.SourceFile}]{paramDesc}");
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static int GetStepTypeOrder(string stepType) => stepType switch
    {
        "Given" => 1,
        "When" => 2,
        "Then" => 3,
        _ => 4
    };

    private string GetStepDefinitionsDirectory()
    {
        var testsProjectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _config.TestsProjectPath));
        if (!Directory.Exists(testsProjectDir) && Directory.Exists(Path.GetFullPath(_config.TestsProjectPath)))
        {
            testsProjectDir = Path.GetFullPath(_config.TestsProjectPath);
        }
        return Path.Combine(testsProjectDir, "StepDefinitions");
    }

    internal static List<StepDefinitionInfo> ParseStepDefinitionsFromSource(string sourceCode, string fileName)
    {
        var results = new List<StepDefinitionInfo>();
        var lines = sourceCode.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        var pendingAttributes = new List<(string StepType, string RawPattern, int LineNumber)>();

        // Regex for SpecFlow step attributes: [Given(@"...")] or [When("...")]
        var attrRegex = new Regex(@"\[\s*(Given|When|Then|StepDefinition)\s*\(\s*(?:@""(?<verbatim>(?:""""|[^""])*)""|""(?<regular>(?:\\.|[^""\\])*)"")\s*\)\s*\]", RegexOptions.Compiled);

        // Regex for method declaration
        var methodRegex = new Regex(@"\bpublic\s+(?:async\s+)?(?:Task(?:\<[^\>]*\>)?|void)\s+(?<methodName>\w+)\s*\((?<params>[^\)]*)\)", RegexOptions.Compiled);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            var attrMatch = attrRegex.Match(trimmed);
            if (attrMatch.Success)
            {
                var stepType = attrMatch.Groups[1].Value;
                string pattern;
                if (attrMatch.Groups["verbatim"].Success && !string.IsNullOrEmpty(attrMatch.Groups["verbatim"].Value))
                {
                    pattern = attrMatch.Groups["verbatim"].Value.Replace("\"\"", "\"");
                }
                else
                {
                    pattern = Regex.Unescape(attrMatch.Groups["regular"].Value);
                }

                pendingAttributes.Add((stepType, pattern, i + 1));
                continue;
            }

            if (pendingAttributes.Count > 0)
            {
                var methodMatch = methodRegex.Match(trimmed);
                if (methodMatch.Success)
                {
                    var methodName = methodMatch.Groups["methodName"].Value;
                    var rawParams = methodMatch.Groups["params"].Value;
                    var parsedParams = ParseParameters(rawParams);

                    foreach (var pending in pendingAttributes)
                    {
                        var normalized = NormalizePattern(pending.RawPattern, parsedParams);
                        var snippet = BuildAutocompleteSnippet(pending.RawPattern, parsedParams);

                        results.Add(new StepDefinitionInfo
                        {
                            StepType = pending.StepType,
                            Pattern = pending.RawPattern,
                            NormalizedPattern = normalized,
                            AutocompleteSnippet = snippet,
                            MethodName = methodName,
                            Parameters = parsedParams,
                            SourceFile = fileName,
                            LineNumber = pending.LineNumber
                        });
                    }

                    pendingAttributes.Clear();
                }
                else if (!trimmed.StartsWith("//") && !trimmed.StartsWith("[") && !string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("public") && !trimmed.StartsWith("private"))
                {
                    // If it was another statement without method, don't hold attributes indefinitely
                    if (trimmed.StartsWith("class ") || trimmed.StartsWith("namespace "))
                    {
                        pendingAttributes.Clear();
                    }
                }
            }
        }

        return results;
    }

    private static List<StepParameterInfo> ParseParameters(string rawParams)
    {
        var result = new List<StepParameterInfo>();
        if (string.IsNullOrWhiteSpace(rawParams)) return result;

        var tokens = rawParams.Split(',');
        foreach (var tok in tokens)
        {
            var parts = tok.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var type = parts[0];
                var name = parts[^1];
                result.Add(new StepParameterInfo { Type = type, Name = name });
            }
        }
        return result;
    }

    private static string NormalizePattern(string rawPattern, List<StepParameterInfo> parameters)
    {
        // Replace regex placeholders like (.*), ([^"]*), (\d+) with {paramName}
        var placeholderRegex = new Regex(@"\((?:\.\*|\.\*\?|\[\^""\]\*|\d\+|\\d\+)\)", RegexOptions.Compiled);
        int paramIndex = 0;

        var normalized = placeholderRegex.Replace(rawPattern, m =>
        {
            if (paramIndex < parameters.Count)
            {
                return $"{{{parameters[paramIndex++].Name}}}";
            }
            return $"{{arg{++paramIndex}}}";
        });

        // Strip leading ^ and trailing $ if present in regex
        if (normalized.StartsWith('^')) normalized = normalized[1..];
        if (normalized.EndsWith('$')) normalized = normalized[..^1];

        return normalized.Trim();
    }

    private static string BuildAutocompleteSnippet(string rawPattern, List<StepParameterInfo> parameters)
    {
        var placeholderRegex = new Regex(@"\((?:\.\*|\.\*\?|\[\^""\]\*|\d\+|\\d\+)\)", RegexOptions.Compiled);
        int paramIndex = 0;

        var snippet = placeholderRegex.Replace(rawPattern, m =>
        {
            if (paramIndex < parameters.Count)
            {
                return $"\"{parameters[paramIndex++].Name}\"";
            }
            return $"\"arg{++paramIndex}\"";
        });

        if (snippet.StartsWith('^')) snippet = snippet[1..];
        if (snippet.EndsWith('$')) snippet = snippet[..^1];

        return snippet.Trim();
    }
}
