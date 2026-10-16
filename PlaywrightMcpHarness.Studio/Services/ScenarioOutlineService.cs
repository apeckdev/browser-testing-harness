using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlaywrightMcpHarness.Studio.Models;

namespace PlaywrightMcpHarness.Studio.Services;

public interface IScenarioOutlineService
{
    List<DetectedScenario> ExtractScenariosFromGherkin(string gherkinContent);
    Task<GenerateOutlineResponse> GenerateOutlineAsync(
        GenerateOutlineRequest request,
        string? providerKey = null,
        LlmProviderConfig? customOverride = null,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> StreamGenerateOutlineAsync(
        GenerateOutlineRequest request,
        string? providerKey = null,
        LlmProviderConfig? customOverride = null,
        CancellationToken cancellationToken = default);
    string ReplaceScenarioInGherkin(string fullContent, DetectedScenario targetScenario, string replacementOutline);
    string SanitizeGherkinOutput(string raw);
}

public class ScenarioOutlineService : IScenarioOutlineService
{
    private readonly HarnessConfig _config;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IStepCatalogService _stepCatalog;
    private readonly ILogger<ScenarioOutlineService> _logger;

    public ScenarioOutlineService(
        IOptions<HarnessConfig> configOptions,
        IChatClientFactory chatClientFactory,
        IStepCatalogService stepCatalog,
        ILogger<ScenarioOutlineService> logger)
    {
        _config = configOptions.Value;
        _chatClientFactory = chatClientFactory;
        _stepCatalog = stepCatalog;
        _logger = logger;
    }

    public List<DetectedScenario> ExtractScenariosFromGherkin(string gherkinContent)
    {
        var results = new List<DetectedScenario>();
        if (string.IsNullOrWhiteSpace(gherkinContent)) return results;

        var lines = gherkinContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        DetectedScenario? current = null;
        var currentText = new StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            var isScenarioOutline = trimmed.StartsWith("Scenario Outline:", StringComparison.OrdinalIgnoreCase);
            var isScenario = !isScenarioOutline && trimmed.StartsWith("Scenario:", StringComparison.OrdinalIgnoreCase);

            if (isScenario || isScenarioOutline)
            {
                if (current != null)
                {
                    current.EndLine = i;
                    current.RawText = currentText.ToString().TrimEnd();
                    results.Add(current);
                    currentText.Clear();
                }

                var title = isScenarioOutline
                    ? trimmed.Substring("Scenario Outline:".Length).Trim()
                    : trimmed.Substring("Scenario:".Length).Trim();

                current = new DetectedScenario
                {
                    Title = string.IsNullOrWhiteSpace(title) ? $"Scenario #{results.Count + 1}" : title,
                    StartLine = i + 1,
                    IsOutline = isScenarioOutline
                };

                currentText.AppendLine(line);
            }
            else if (current != null)
            {
                if (IsTagLineForUpcomingScenario(lines, i))
                {
                    current.EndLine = i;
                    current.RawText = currentText.ToString().TrimEnd();
                    results.Add(current);
                    current = null;
                    currentText.Clear();
                }
                else
                {
                    currentText.AppendLine(line);
                }
            }
        }

        if (current != null)
        {
            current.EndLine = lines.Length;
            current.RawText = currentText.ToString().TrimEnd();
            results.Add(current);
        }

        return results;
    }

    private static bool IsTagLineForUpcomingScenario(string[] lines, int currentIndex)
    {
        var trimmed = lines[currentIndex].Trim();
        if (!trimmed.StartsWith("@")) return false;

        for (int k = currentIndex + 1; k < lines.Length; k++)
        {
            var nextTrim = lines[k].Trim();
            if (string.IsNullOrWhiteSpace(nextTrim)) continue;
            if (nextTrim.StartsWith("@")) continue;
            if (nextTrim.StartsWith("Scenario:", StringComparison.OrdinalIgnoreCase) ||
                nextTrim.StartsWith("Scenario Outline:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }
        return false;
    }

    public async IAsyncEnumerable<string> StreamGenerateOutlineAsync(
        GenerateOutlineRequest request,
        string? providerKey = null,
        LlmProviderConfig? customOverride = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chatClient = _chatClientFactory.CreateClient(providerKey, customOverride);
        var categoriesPrompt = BuildCategoriesPrompt(request.Categories);
        var stepCatalogSummary = _stepCatalog.GetCatalogSummaryForPrompt();

        var prompt = $@"You are a Principal QA Automation Architect specializing in SpecFlow and Playwright.
Convert the following single Gherkin scenario into a comprehensive parameterized 'Scenario Outline:' with an 'Examples:' table covering critical edge cases, boundary conditions, and real-world inputs.

=== TARGET SCENARIO ===
{request.ScenarioText}

=== SELECTED TEST DATA CATEGORIES ===
{categoriesPrompt}

=== INDEXED STEP DEFINITIONS IN TEST SUITE ===
{stepCatalogSummary}

=== CRITICAL GHERKIN & SPECFLOW RULES ===
1. Begin immediately with: '  Scenario Outline: <Descriptive Title covering Edge Cases>'
2. Replace concrete strings and arguments with parameter tags like '<name>', '<email>', '<message>', etc.
3. Keep step text identical or closely matching the existing SpecFlow step definitions above so no new C# step methods are needed.
4. Follow with an 'Examples:' table.
5. In the 'Examples:' table, ALWAYS include a 'CaseDescription' column as the first column describing each test case clearly.
6. Generate up to {request.MaxExamples} high-value example rows spanning the selected test data categories.
7. Format table pipes '|' cleanly with proper alignment and spacing.
8. OUTPUT ONLY THE RAW GHERKIN CODE FOR THE SCENARIO OUTLINE. Do NOT include markdown code blocks (```gherkin), introductory banter, or explanations. Start directly with '  Scenario Outline:'.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are an automated Gherkin Scenario Outline generator. You output only valid SpecFlow Scenario Outline Gherkin text with Examples tables. Do not include markdown code blocks, conversational commentary, or preamble."),
            new(ChatRole.User, prompt)
        };

        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, cancellationToken: cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
    }

    public async Task<GenerateOutlineResponse> GenerateOutlineAsync(
        GenerateOutlineRequest request,
        string? providerKey = null,
        LlmProviderConfig? customOverride = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var chatClient = _chatClientFactory.CreateClient(providerKey, customOverride);
            var categoriesPrompt = BuildCategoriesPrompt(request.Categories);
            var stepCatalogSummary = _stepCatalog.GetCatalogSummaryForPrompt();

            var prompt = $@"You are a Principal QA Automation Architect specializing in SpecFlow and Playwright.
Convert the following single Gherkin scenario into a comprehensive parameterized 'Scenario Outline:' with an 'Examples:' table covering critical edge cases, boundary conditions, and real-world inputs.

=== TARGET SCENARIO ===
{request.ScenarioText}

=== SELECTED TEST DATA CATEGORIES ===
{categoriesPrompt}

=== INDEXED STEP DEFINITIONS IN TEST SUITE ===
{stepCatalogSummary}

=== CRITICAL GHERKIN & SPECFLOW RULES ===
1. Begin immediately with: '  Scenario Outline: <Descriptive Title covering Edge Cases>'
2. Replace concrete strings and arguments with parameter tags like '<name>', '<email>', '<message>', etc.
3. Keep step text identical or closely matching the existing SpecFlow step definitions above so no new C# step methods are needed.
4. Follow with an 'Examples:' table.
5. In the 'Examples:' table, ALWAYS include a 'CaseDescription' column as the first column describing each test case clearly.
6. Generate up to {request.MaxExamples} high-value example rows spanning the selected test data categories.
7. Format table pipes '|' cleanly with proper alignment and spacing.
8. OUTPUT ONLY THE RAW GHERKIN CODE FOR THE SCENARIO OUTLINE. Do NOT include markdown code blocks (```gherkin), introductory banter, or explanations. Start directly with '  Scenario Outline:'.";

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "You are an automated Gherkin Scenario Outline generator. You output only valid SpecFlow Scenario Outline Gherkin text with Examples tables without markdown formatting or conversational commentary."),
                new(ChatRole.User, prompt)
            };

            var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var rawResult = response.Text ?? "";

            var cleanGherkin = SanitizeGherkinOutput(rawResult);
            if (string.IsNullOrWhiteSpace(cleanGherkin))
            {
                return new GenerateOutlineResponse
                {
                    Success = false,
                    ErrorMessage = "LLM returned an empty response."
                };
            }

            return new GenerateOutlineResponse
            {
                Success = true,
                GeneratedOutline = cleanGherkin
            };
        }
        catch (OperationCanceledException)
        {
            return new GenerateOutlineResponse
            {
                Success = false,
                ErrorMessage = "Generation was cancelled."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Scenario Outline.");
            return new GenerateOutlineResponse
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public string ReplaceScenarioInGherkin(string fullContent, DetectedScenario targetScenario, string replacementOutline)
    {
        var newline = fullContent.Contains("\r\n") ? "\r\n" : "\n";
        var lines = fullContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
        var startIndex = targetScenario.StartLine - 1;

        // Verify or locate startIndex if lines shifted or if title matches elsewhere
        if (startIndex < 0 || startIndex >= lines.Count || !lines[startIndex].Contains(targetScenario.Title, StringComparison.OrdinalIgnoreCase))
        {
            var foundIndex = lines.FindIndex(l =>
                (l.TrimStart().StartsWith("Scenario:", StringComparison.OrdinalIgnoreCase) ||
                 l.TrimStart().StartsWith("Scenario Outline:", StringComparison.OrdinalIgnoreCase)) &&
                l.Contains(targetScenario.Title, StringComparison.OrdinalIgnoreCase));

            if (foundIndex >= 0)
            {
                startIndex = foundIndex;
            }
        }

        if (startIndex < 0 || startIndex >= lines.Count)
        {
            // Fallback: append
            return fullContent.TrimEnd() + newline + newline + replacementOutline.Trim() + newline;
        }

        // Determine where this scenario ends (next scenario, tag line for upcoming scenario, rule, or EOF)
        int endIndex = lines.Count;
        for (int i = startIndex + 1; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("Scenario:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Scenario Outline:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Rule:", StringComparison.OrdinalIgnoreCase) ||
                IsTagLineForUpcomingScenario(lines.ToArray(), i))
            {
                endIndex = i;
                break;
            }
        }

        var count = endIndex - startIndex;
        var replacementLines = replacementOutline.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        lines.RemoveRange(startIndex, count);
        lines.InsertRange(startIndex, replacementLines);

        var result = string.Join(newline, lines);
        if (!result.EndsWith("\n") && !result.EndsWith("\r"))
        {
            result += newline;
        }
        return result;
    }

    private static string BuildCategoriesPrompt(EdgeCaseCategoryOptions cats)
    {
        var list = new List<string>();
        if (cats.IncludeHappyPath) list.Add("- Happy Path & Equivalence Classes (standard valid inputs, typical user flows)");
        if (cats.IncludeBoundaryLimits) list.Add("- Boundary Values & Limits (minimum length, maximum field length boundaries, exact boundary thresholds)");
        if (cats.IncludeEmptyAndWhitespace) list.Add("- Empty & Missing Values (empty string \"\", whitespace only \"   \", optional vs required fields)");
        if (cats.IncludeSpecialCharsAndUnicode) list.Add("- Special Characters & Internationalization (accented characters e.g. René, apostrophes O'Connor, punctuation, unicode symbols)");
        if (cats.IncludeSecuritySanity) list.Add("- Security & Malicious Sanity Inputs (HTML/XSS tags <script>, SQL quotes ' OR '1'='1, directory traversal ../)");
        if (cats.IncludeMalformedFormats) list.Add("- Malformed & Invalid Formats (invalid email addresses like missing domain, invalid syntax)");

        return list.Count == 0
            ? "- General edge cases and boundary conditions."
            : string.Join("\n", list);
    }

    public string SanitizeGherkinOutput(string raw) => CleanGherkinOutput(raw);

    public static string CleanGherkinOutput(string raw)
    {
        var cleaned = raw.Trim();

        // Strip markdown code fences if model enclosed response in ```gherkin ... ```
        if (cleaned.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            var firstLineEnd = cleaned.IndexOf('\n');
            if (firstLineEnd > 0)
            {
                cleaned = cleaned.Substring(firstLineEnd + 1);
            }
        }

        if (cleaned.EndsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(0, cleaned.Length - 3);
        }

        return cleaned.Trim();
    }
}
