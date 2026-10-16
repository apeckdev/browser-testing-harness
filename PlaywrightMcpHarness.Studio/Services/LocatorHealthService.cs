using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlaywrightMcpHarness.Studio.Models;

namespace PlaywrightMcpHarness.Studio.Services;

public interface ILocatorHealthService
{
    LocatorHealthReport? CachedReport { get; }
    Task<LocatorHealthReport> AnalyzeTestSuiteLocatorsAsync(bool forceReload = false);
    Task<AutoHealResponse> AutoHealLocatorAsync(
        AutoHealRequest request,
        string? providerKey = null,
        LlmProviderConfig? customOverride = null,
        CancellationToken cancellationToken = default);
    Task<string> ApplyLocatorRefactorAsync(string relativePath, string oldMethodSource, string newMethodSource);
}

public class LocatorHealthService : ILocatorHealthService
{
    private readonly HarnessConfig _config;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly ILogger<LocatorHealthService> _logger;

    private LocatorHealthReport? _cachedReport;
    public LocatorHealthReport? CachedReport => _cachedReport;

    public LocatorHealthService(
        IOptions<HarnessConfig> configOptions,
        IChatClientFactory chatClientFactory,
        ILogger<LocatorHealthService> logger)
    {
        _config = configOptions.Value;
        _chatClientFactory = chatClientFactory;
        _logger = logger;
    }

    public Task<LocatorHealthReport> AnalyzeTestSuiteLocatorsAsync(bool forceReload = false)
    {
        if (_cachedReport != null && !forceReload)
        {
            return Task.FromResult(_cachedReport);
        }

        var report = new LocatorHealthReport();
        var stepDefsDir = GetStepDefinitionsDirectory();

        if (!Directory.Exists(stepDefsDir))
        {
            _logger.LogWarning("StepDefinitions directory not found at: {Path}", stepDefsDir);
            _cachedReport = report;
            return Task.FromResult(report);
        }

        var csFiles = Directory.GetFiles(stepDefsDir, "*Steps.cs", SearchOption.AllDirectories);
        var allFindings = new List<LocatorFinding>();

        foreach (var file in csFiles)
        {
            try
            {
                var content = File.ReadAllText(file);
                var fileName = Path.GetFileName(file);
                var relativePath = Path.GetRelativePath(stepDefsDir, file);
                var findings = ParseLocatorsFromSource(content, fileName, relativePath);
                allFindings.AddRange(findings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning step file for locators: {File}", file);
            }
        }

        report.Findings = allFindings;
        report.TotalLocators = allFindings.Count;
        report.ResilientCount = allFindings.Count(f => f.HealthTier == LocatorHealthTier.Resilient);
        report.CautionCount = allFindings.Count(f => f.HealthTier == LocatorHealthTier.Caution);
        report.FragileCount = allFindings.Count(f => f.HealthTier == LocatorHealthTier.Fragile);

        if (report.TotalLocators > 0)
        {
            var averageScore = (int)Math.Round(allFindings.Average(f => f.HealthScore));
            report.OverallScore = Math.Clamp(averageScore, 0, 100);
            report.HealthGrade = CalculateGrade(report.OverallScore);
        }
        else
        {
            report.OverallScore = 100;
            report.HealthGrade = "A+";
        }

        report.ScannedAt = DateTime.UtcNow;
        _cachedReport = report;
        return Task.FromResult(report);
    }

    public async Task<AutoHealResponse> AutoHealLocatorAsync(
        AutoHealRequest request,
        string? providerKey = null,
        LlmProviderConfig? customOverride = null,
        CancellationToken cancellationToken = default)
    {
        var finding = request.Finding;
        if (finding == null || string.IsNullOrWhiteSpace(finding.FullMethodSource))
        {
            return new AutoHealResponse
            {
                Success = false,
                ErrorMessage = "Finding or method source is missing."
            };
        }

        try
        {
            var chatClient = _chatClientFactory.CreateClient(providerKey, customOverride);

            var prompt = $@"You are a Principal Playwright and SpecFlow Quality Engineer.
Refactor the following C# SpecFlow step method to eliminate fragile, anti-pattern locators and replace them with resilient, user-facing Playwright locators adhering strictly to official Playwright best practices.

=== TARGET LOCATOR TO REFACTOR ===
Locator: {finding.RawExpression}
Issue: {finding.IssueReason}
Recommended Approach: {finding.Recommendation}
{(string.IsNullOrWhiteSpace(request.UserGuidance) ? "" : $"User Guidance: {request.UserGuidance}\n")}

=== ORIGINAL METHOD CODE ({finding.SourceFile}:L{finding.LineNumber}) ===
{finding.FullMethodSource}

=== PLAYWRIGHT BEST PRACTICES RULES ===
1. Prioritize user-facing, semantic Playwright locators:
   - Page.GetByRole(AriaRole.<Role>, new() {{ Name = ""<Name>"" }}) for buttons, links, headings, checkboxes, etc.
   - Page.GetByLabel(""<Label>"") for form fields with labels.
   - Page.GetByPlaceholder(""<Placeholder>"") for inputs with placeholder text.
   - Page.GetByText(""<Text>"") for non-interactive text elements.
   - Page.GetByTestId(""<id>"") when no accessible user-facing attribute exists.
2. ELIMINATE brittle selectors: CSS nth-child, deep structural paths (div > ul > li), complex XPaths, and unadorned tag selectors.
3. Keep the step binding attribute (e.g. [Given(...)], [When(...)], [Then(...)]) and the method signature identical.
4. Keep all assertions, parameters, and other logic intact.
5. FORMAT YOUR RESPONSE EXACTLY AS FOLLOWS:
```csharp
<Your Refactored Method Code here>
```
EXPLANATION:
<1-2 sentences explaining why the new locator is more resilient and user-facing>";

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "You are an automated Playwright C# refactoring tool. Output the refactored method inside a ```csharp code fence, followed by an EXPLANATION: section."),
                new(ChatRole.User, prompt)
            };

            var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var rawText = response.Text ?? "";

            var refactoredCode = ExtractCodeFence(rawText, "csharp");
            var explanation = ExtractExplanation(rawText);

            if (string.IsNullOrWhiteSpace(refactoredCode))
            {
                refactoredCode = rawText.Trim();
            }

            return new AutoHealResponse
            {
                Success = true,
                OriginalMethodCode = finding.FullMethodSource,
                RefactoredMethodCode = refactoredCode,
                Explanation = string.IsNullOrWhiteSpace(explanation)
                    ? "Replaced fragile selector with semantic Playwright user-facing locator."
                    : explanation
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-heal locator.");
            return new AutoHealResponse
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<string> ApplyLocatorRefactorAsync(string relativePath, string oldMethodSource, string newMethodSource)
    {
        var stepDefsDir = GetStepDefinitionsDirectory();
        var fullPath = Path.Combine(stepDefsDir, relativePath);

        if (!File.Exists(fullPath))
        {
            // Try direct path search
            var altPath = Path.Combine(stepDefsDir, Path.GetFileName(relativePath));
            if (File.Exists(altPath))
            {
                fullPath = altPath;
            }
            else
            {
                throw new FileNotFoundException($"Step definition file not found: {relativePath}");
            }
        }

        var content = await File.ReadAllTextAsync(fullPath);
        var normalizedContent = NormalizeWhitespace(content);
        var normalizedOld = NormalizeWhitespace(oldMethodSource);

        string updatedContent;
        if (content.Contains(oldMethodSource.Trim()))
        {
            updatedContent = content.Replace(oldMethodSource.Trim(), newMethodSource.Trim());
        }
        else if (normalizedContent.Contains(normalizedOld))
        {
            // Fallback line-by-line replacement
            updatedContent = ReplaceFuzzyMethod(content, oldMethodSource, newMethodSource);
        }
        else
        {
            throw new InvalidOperationException("Could not locate original method body in file to replace. Please check for recent edits.");
        }

        await File.WriteAllTextAsync(fullPath, updatedContent);
        await AnalyzeTestSuiteLocatorsAsync(forceReload: true);
        return $"Successfully refactored locator in {Path.GetFileName(fullPath)}.";
    }

    internal static List<LocatorFinding> ParseLocatorsFromSource(string sourceCode, string fileName, string relativePath)
    {
        var findings = new List<LocatorFinding>();
        if (string.IsNullOrWhiteSpace(sourceCode)) return findings;

        var lines = sourceCode.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        // Track methods: [Binding] methods marked with [Given], [When], [Then]
        int currentMethodStart = -1;
        string currentMethodName = "";
        string currentStepAttr = "";
        var currentMethodLines = new List<string>();
        int openBraces = 0;
        bool inMethod = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // Detect SpecFlow binding attributes
            if (!inMethod && (trimmed.StartsWith("[Given(") || trimmed.StartsWith("[When(") || trimmed.StartsWith("[Then(") || trimmed.StartsWith("[StepDefinition(")))
            {
                currentStepAttr = trimmed;
                currentMethodStart = i;
                currentMethodLines.Clear();
                currentMethodLines.Add(line);
                continue;
            }

            if (currentMethodStart >= 0 && !inMethod)
            {
                currentMethodLines.Add(line);
                if (trimmed.Contains("public async Task") || trimmed.Contains("public void") || trimmed.Contains("public Task"))
                {
                    var match = Regex.Match(trimmed, @"public\s+(?:async\s+)?(?:Task<[\w\s,]+>|Task|void)\s+(\w+)\s*\(");
                    if (match.Success)
                    {
                        currentMethodName = match.Groups[1].Value;
                    }
                    if (trimmed.Contains("{"))
                    {
                        inMethod = true;
                        openBraces = 1;
                    }
                }
                else if (trimmed.StartsWith("{"))
                {
                    inMethod = true;
                    openBraces = 1;
                }
                continue;
            }

            if (inMethod)
            {
                currentMethodLines.Add(line);
                foreach (char c in trimmed)
                {
                    if (c == '{') openBraces++;
                    else if (c == '}') openBraces--;
                }

                // Scan this line for Playwright locators!
                InspectLineForLocators(line, i + 1, fileName, relativePath, currentMethodName, currentStepAttr, currentMethodLines, findings);

                if (openBraces <= 0)
                {
                    // Method closed
                    var methodBody = string.Join("\n", currentMethodLines);
                    foreach (var f in findings.Where(f => f.SourceFile == fileName && f.MethodName == currentMethodName && string.IsNullOrEmpty(f.FullMethodSource)))
                    {
                        f.FullMethodSource = methodBody;
                    }

                    inMethod = false;
                    currentMethodStart = -1;
                    currentMethodName = "";
                    currentStepAttr = "";
                    currentMethodLines.Clear();
                    openBraces = 0;
                }
            }
        }

        return findings;
    }

    private static void InspectLineForLocators(
        string line,
        int lineNumber,
        string fileName,
        string relativePath,
        string methodName,
        string stepAttr,
        List<string> currentMethodLines,
        List<LocatorFinding> findings)
    {
        var trimmed = line.Trim();

        // 1. GetByRole
        if (trimmed.Contains("GetByRole("))
        {
            var match = Regex.Match(trimmed, @"(?:_page|Page|[\w]+)\.GetByRole\(([^)]+(?:\([^)]*\))*[^)]*)\)");
            var expr = match.Success ? match.Value : "GetByRole(...)";
            findings.Add(new LocatorFinding
            {
                SourceFile = fileName,
                RelativePath = relativePath,
                LineNumber = lineNumber,
                MethodName = methodName,
                StepAttribute = stepAttr,
                RawExpression = expr,
                LocatorType = "GetByRole",
                HealthTier = LocatorHealthTier.Resilient,
                HealthScore = 100,
                IssueReason = "Optimal user-facing semantic role locator adhering to Playwright best practices.",
                Recommendation = "No refactoring needed. Excellent locator resilience."
            });
            return;
        }

        // 2. GetByLabel
        if (trimmed.Contains("GetByLabel("))
        {
            var match = Regex.Match(trimmed, @"(?:_page|Page|[\w]+)\.GetByLabel\(([^)]+)\)");
            var expr = match.Success ? match.Value : "GetByLabel(...)";
            findings.Add(new LocatorFinding
            {
                SourceFile = fileName,
                RelativePath = relativePath,
                LineNumber = lineNumber,
                MethodName = methodName,
                StepAttribute = stepAttr,
                RawExpression = expr,
                LocatorType = "GetByLabel",
                HealthTier = LocatorHealthTier.Resilient,
                HealthScore = 100,
                IssueReason = "Optimal form control label locator adhering to Playwright best practices.",
                Recommendation = "No refactoring needed. Excellent locator resilience."
            });
            return;
        }

        // 3. GetByPlaceholder
        if (trimmed.Contains("GetByPlaceholder("))
        {
            var match = Regex.Match(trimmed, @"(?:_page|Page|[\w]+)\.GetByPlaceholder\(([^)]+)\)");
            var expr = match.Success ? match.Value : "GetByPlaceholder(...)";
            findings.Add(new LocatorFinding
            {
                SourceFile = fileName,
                RelativePath = relativePath,
                LineNumber = lineNumber,
                MethodName = methodName,
                StepAttribute = stepAttr,
                RawExpression = expr,
                LocatorType = "GetByPlaceholder",
                HealthTier = LocatorHealthTier.Resilient,
                HealthScore = 95,
                IssueReason = "User-facing placeholder locator.",
                Recommendation = "Good resilient locator. Prefer GetByLabel if an explicit <label> is present."
            });
            return;
        }

        // 4. GetByTestId
        if (trimmed.Contains("GetByTestId("))
        {
            var match = Regex.Match(trimmed, @"(?:_page|Page|[\w]+)\.GetByTestId\(([^)]+)\)");
            var expr = match.Success ? match.Value : "GetByTestId(...)";
            findings.Add(new LocatorFinding
            {
                SourceFile = fileName,
                RelativePath = relativePath,
                LineNumber = lineNumber,
                MethodName = methodName,
                StepAttribute = stepAttr,
                RawExpression = expr,
                LocatorType = "GetByTestId",
                HealthTier = LocatorHealthTier.Resilient,
                HealthScore = 100,
                IssueReason = "Dedicated automated test contract attribute (data-testid). Highly resilient against layout and text changes.",
                Recommendation = "No refactoring needed. Excellent locator resilience."
            });
            return;
        }

        // 5. GetByText
        if (trimmed.Contains("GetByText("))
        {
            var match = Regex.Match(trimmed, @"(?:_page|Page|[\w]+)\.GetByText\(([^)]+)\)");
            var expr = match.Success ? match.Value : "GetByText(...)";
            findings.Add(new LocatorFinding
            {
                SourceFile = fileName,
                RelativePath = relativePath,
                LineNumber = lineNumber,
                MethodName = methodName,
                StepAttribute = stepAttr,
                RawExpression = expr,
                LocatorType = "GetByText",
                HealthTier = LocatorHealthTier.Resilient,
                HealthScore = 90,
                IssueReason = "User-facing text locator.",
                Recommendation = "Good for non-interactive content. For interactive elements (buttons, links), prefer GetByRole."
            });
            return;
        }

        // 6. GetByAltText / GetByTitle
        if (trimmed.Contains("GetByAltText(") || trimmed.Contains("GetByTitle("))
        {
            var type = trimmed.Contains("GetByAltText") ? "GetByAltText" : "GetByTitle";
            findings.Add(new LocatorFinding
            {
                SourceFile = fileName,
                RelativePath = relativePath,
                LineNumber = lineNumber,
                MethodName = methodName,
                StepAttribute = stepAttr,
                RawExpression = trimmed,
                LocatorType = type,
                HealthTier = LocatorHealthTier.Resilient,
                HealthScore = 90,
                IssueReason = $"User-facing attribute ({type}) adhering to Playwright best practices.",
                Recommendation = "Resilient locator for media/titled elements."
            });
            return;
        }

        // 7. Generic Locator(...) or QuerySelectorAsync(...)
        if (trimmed.Contains(".Locator(") || trimmed.Contains(".QuerySelectorAsync(") || trimmed.Contains(".QuerySelector("))
        {
            var match = Regex.Match(trimmed, @"(?:_page|Page|[\w]+)\.(?:Locator|QuerySelectorAsync|QuerySelector)\s*\(\s*""([^""]+)""");
            var selector = match.Success ? match.Groups[1].Value : trimmed;
            var rawExpr = match.Success ? match.Value + ")" : trimmed;

            // Classify selector fragility
            ClassifyGenericSelector(selector, rawExpr, lineNumber, fileName, relativePath, methodName, stepAttr, findings);
        }
    }

    private static void ClassifyGenericSelector(
        string selector,
        string rawExpr,
        int lineNumber,
        string fileName,
        string relativePath,
        string methodName,
        string stepAttr,
        List<LocatorFinding> findings)
    {
        // A) Test IDs inside Locator("[data-testid=...]")
        if (selector.Contains("data-testid") || selector.Contains("data-cy") || selector.Contains("data-test"))
        {
            findings.Add(new LocatorFinding
            {
                SourceFile = fileName,
                RelativePath = relativePath,
                LineNumber = lineNumber,
                MethodName = methodName,
                StepAttribute = stepAttr,
                RawExpression = rawExpr,
                LocatorType = "CSS (Test ID)",
                HealthTier = LocatorHealthTier.Resilient,
                HealthScore = 95,
                IssueReason = "Uses dedicated test attribute inside CSS selector.",
                Recommendation = "Resilient. Consider migrating syntax to Page.GetByTestId(...) for cleaner code."
            });
            return;
        }

        // B) Absolute or deep XPath
        if (selector.StartsWith("//") || selector.StartsWith("xpath=", StringComparison.OrdinalIgnoreCase) || selector.Contains("//"))
        {
            findings.Add(new LocatorFinding
            {
                SourceFile = fileName,
                RelativePath = relativePath,
                LineNumber = lineNumber,
                MethodName = methodName,
                StepAttribute = stepAttr,
                RawExpression = rawExpr,
                LocatorType = "XPath",
                HealthTier = LocatorHealthTier.Fragile,
                HealthScore = 20,
                IssueReason = "Complex or absolute XPath selector. XPath queries couple tests to exact DOM depth and break upon layout refactoring.",
                Recommendation = "Replace with user-facing Page.GetByRole(...) or Page.GetByLabel(...)."
            });
            return;
        }

        // C) Deep structural hierarchy or nth-child
        if (selector.Contains(":nth-child") || selector.Contains(":nth-of-type") || Regex.IsMatch(selector, @"\w+\s*>\s*\w+\s*>\s*\w+"))
        {
            findings.Add(new LocatorFinding
            {
                SourceFile = fileName,
                RelativePath = relativePath,
                LineNumber = lineNumber,
                MethodName = methodName,
                StepAttribute = stepAttr,
                RawExpression = rawExpr,
                LocatorType = "Structural CSS",
                HealthTier = LocatorHealthTier.Fragile,
                HealthScore = 15,
                IssueReason = "Brittle structural DOM hierarchy (:nth-child or deep nesting). High failure rate when sibling order or parent containers change.",
                Recommendation = "Replace with Page.GetByRole(...) or semantic user-facing filter."
            });
            return;
        }

        // D) Auto-generated / hashed CSS classes
        if (Regex.IsMatch(selector, @"\.(?:css|sc|jss|style)-[a-zA-Z0-9]{4,}") || selector.Contains("[class*="))
        {
            findings.Add(new LocatorFinding
            {
                SourceFile = fileName,
                RelativePath = relativePath,
                LineNumber = lineNumber,
                MethodName = methodName,
                StepAttribute = stepAttr,
                RawExpression = rawExpr,
                LocatorType = "Hashed CSS Class",
                HealthTier = LocatorHealthTier.Fragile,
                HealthScore = 25,
                IssueReason = "Auto-generated hashed class selector (e.g. styled-components / CSS modules). Changes dynamically between application builds.",
                Recommendation = "Replace with user-facing Page.GetByRole(...) or data-testid attribute."
            });
            return;
        }

        // E) Bare HTML tag names
        if (Regex.IsMatch(selector.Trim(), @"^(?:button|input|select|textarea|a|form|h1|h2|h3|h4|div|span|p|table)$", RegexOptions.IgnoreCase))
        {
            findings.Add(new LocatorFinding
            {
                SourceFile = fileName,
                RelativePath = relativePath,
                LineNumber = lineNumber,
                MethodName = methodName,
                StepAttribute = stepAttr,
                RawExpression = rawExpr,
                LocatorType = "Bare Tag Selector",
                HealthTier = LocatorHealthTier.Fragile,
                HealthScore = 30,
                IssueReason = "Unqualified bare HTML tag selector without contextual attributes. Highly prone to strict-mode multiple element violations.",
                Recommendation = "Replace with Page.GetByRole(AriaRole.<Role>, new() { Name = \"...\" }) to uniquely target the intended control."
            });
            return;
        }

        // F) Clean #ID selector
        if (selector.StartsWith("#") && !selector.Contains(" ") && !selector.Contains(">"))
        {
            findings.Add(new LocatorFinding
            {
                SourceFile = fileName,
                RelativePath = relativePath,
                LineNumber = lineNumber,
                MethodName = methodName,
                StepAttribute = stepAttr,
                RawExpression = rawExpr,
                LocatorType = "ID Selector",
                HealthTier = LocatorHealthTier.Caution,
                HealthScore = 70,
                IssueReason = "ID selector is functional, but bypasses user accessibility contracts. IDs may be dynamically generated in modern SPA frameworks.",
                Recommendation = "Prefer user-facing Page.GetByRole or Page.GetByLabel for higher testing fidelity."
            });
            return;
        }

        // G) Semantic attribute selector (e.g. input[name="..."])
        if (selector.Contains("[name=") || selector.Contains("[placeholder="))
        {
            findings.Add(new LocatorFinding
            {
                SourceFile = fileName,
                RelativePath = relativePath,
                LineNumber = lineNumber,
                MethodName = methodName,
                StepAttribute = stepAttr,
                RawExpression = rawExpr,
                LocatorType = "Attribute Selector",
                HealthTier = LocatorHealthTier.Caution,
                HealthScore = 75,
                IssueReason = "Form attribute selector. Better than structural CSS, but GetByLabel or GetByRole is cleaner.",
                Recommendation = "Consider migrating to Page.GetByLabel(...) or Page.GetByRole(AriaRole.Textbox, ...)."
            });
            return;
        }

        // H) Default fallback
        findings.Add(new LocatorFinding
        {
            SourceFile = fileName,
            RelativePath = relativePath,
            LineNumber = lineNumber,
            MethodName = methodName,
            StepAttribute = stepAttr,
            RawExpression = rawExpr,
            LocatorType = "CSS Selector",
            HealthTier = LocatorHealthTier.Caution,
            HealthScore = 60,
            IssueReason = "Generic CSS selector. May couple tests to implementation details rather than user experience.",
            Recommendation = "Evaluate whether a semantic user-facing locator (GetByRole, GetByText) can represent this user action."
        });
    }

    private static string CalculateGrade(int score) => score switch
    {
        >= 95 => "A+",
        >= 90 => "A",
        >= 80 => "B",
        >= 70 => "C",
        >= 60 => "D",
        _ => "F"
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

    private static string ExtractCodeFence(string text, string lang)
    {
        var pattern = $@"```{lang}\s*([\s\S]*?)```";
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        // Fallback generic code fence
        var genericMatch = Regex.Match(text, @"```\s*([\s\S]*?)```");
        if (genericMatch.Success)
        {
            return genericMatch.Groups[1].Value.Trim();
        }

        return "";
    }

    private static string ExtractExplanation(string text)
    {
        var marker = "EXPLANATION:";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            return text.Substring(idx + marker.Length).Trim();
        }
        return "";
    }

    private static string NormalizeWhitespace(string s) =>
        Regex.Replace(s, @"\s+", " ").Trim();

    private static string ReplaceFuzzyMethod(string fullContent, string oldMethod, string newMethod)
    {
        var lines = fullContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
        var oldLines = oldMethod.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();

        if (oldLines.Count == 0) return fullContent;

        var firstLine = oldLines[0];
        int startIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim() == firstLine)
            {
                startIdx = i;
                break;
            }
        }

        if (startIdx < 0)
        {
            return fullContent;
        }

        var lastLine = oldLines.Last();
        int endIdx = -1;
        for (int i = startIdx; i < lines.Count; i++)
        {
            if (lines[i].Trim() == lastLine)
            {
                endIdx = i;
                break;
            }
        }

        if (endIdx < 0) endIdx = Math.Min(startIdx + oldLines.Count, lines.Count - 1);

        var count = endIdx - startIdx + 1;
        var replacementLines = newMethod.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        lines.RemoveRange(startIdx, count);
        lines.InsertRange(startIdx, replacementLines);

        var newline = fullContent.Contains("\r\n") ? "\r\n" : "\n";
        return string.Join(newline, lines);
    }
}
