using System;
using System.Collections.Generic;
using System.Diagnostics;
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

public interface IPageObjectService
{
    List<PageObjectInfo> CachedPageObjects { get; }
    Task<List<PageObjectInfo>> GetPageObjectsAsync(bool forceReload = false);
    Task<ExtractPageObjectResponse> ExtractPageObjectFromStepsAsync(
        ExtractPageObjectRequest request,
        string? providerKey = null,
        LlmProviderConfig? customOverride = null,
        CancellationToken cancellationToken = default);
    Task<string> ApplyPageObjectExtractionAsync(
        string stepFileRelativePath,
        string pageObjectFileName,
        string pageObjectCode,
        string refactoredStepsCode);
}

public class PageObjectService : IPageObjectService
{
    private readonly HarnessConfig _config;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly ILogger<PageObjectService> _logger;

    private List<PageObjectInfo> _cachedPageObjects = new();
    public List<PageObjectInfo> CachedPageObjects => _cachedPageObjects;

    public PageObjectService(
        IOptions<HarnessConfig> configOptions,
        IChatClientFactory chatClientFactory,
        ILogger<PageObjectService> logger)
    {
        _config = configOptions.Value;
        _chatClientFactory = chatClientFactory;
        _logger = logger;
    }

    public Task<List<PageObjectInfo>> GetPageObjectsAsync(bool forceReload = false)
    {
        if (_cachedPageObjects.Count > 0 && !forceReload)
        {
            return Task.FromResult(_cachedPageObjects);
        }

        var result = new List<PageObjectInfo>();
        var root = GetTestsProjectRoot();
        var pagesDir = GetPagesDirectory();

        if (!Directory.Exists(pagesDir))
        {
            _cachedPageObjects = result;
            return Task.FromResult(result);
        }

        var files = Directory.GetFiles(pagesDir, "*.cs", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            try
            {
                var content = File.ReadAllText(file);
                var fileName = Path.GetFileName(file);
                var relPath = Path.GetRelativePath(root, file);

                var po = ParsePageObjectFromSource(content, fileName, relPath);
                result.Add(po);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing Page Object file: {File}", file);
            }
        }

        _cachedPageObjects = result;
        return Task.FromResult(result);
    }

    public async Task<ExtractPageObjectResponse> ExtractPageObjectFromStepsAsync(
        ExtractPageObjectRequest request,
        string? providerKey = null,
        LlmProviderConfig? customOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SourceStepDefinitionCode))
        {
            return new ExtractPageObjectResponse
            {
                Success = false,
                ErrorMessage = "Step definition code is empty."
            };
        }

        var baseName = Path.GetFileNameWithoutExtension(request.SourceStepDefinitionFile);
        if (baseName.EndsWith("Steps", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName.Substring(0, baseName.Length - 5);
        }

        var targetClassName = string.IsNullOrWhiteSpace(request.SuggestedPageObjectName)
            ? $"{baseName}Page"
            : (request.SuggestedPageObjectName.EndsWith("Page") ? request.SuggestedPageObjectName : $"{request.SuggestedPageObjectName}Page");

        var targetFileName = $"{targetClassName}.cs";

        try
        {
            var chatClient = _chatClientFactory.CreateClient(providerKey, customOverride);

            var prompt = $@"You are a Principal Playwright and SpecFlow Architect.
Refactor the following SpecFlow C# step definition class to use the enterprise Page Object Model (POM) pattern.

=== GOALS ===
1. Create a new Page Object class named '{targetClassName}' in namespace 'PlaywrightMcpHarness.Tests.Pages'.
   - Constructor receives 'IPage page' via dependency injection.
   - Encapsulates all Playwright locators (e.g., GetByRole, GetByLabel, GetByText) and interaction logic into clean, async action and assertion methods.
   - Uses official Playwright best practices (semantic user-facing roles/labels, no brittle CSS/XPath).
2. Refactor the Step Definition class to use constructor injection of '{targetClassName}'.
   - Replaces the direct 'IPage' dependency with '{targetClassName}'. SpecFlow's BoDi DI container will automatically instantiate the page object with IPage!
   - Step methods should become clean 1-2 line delegations to the Page Object methods.
   - Keep all SpecFlow binding attributes ([Given(...)], [When(...)], [Then(...)]) unchanged!

{(string.IsNullOrWhiteSpace(request.UserGuidance) ? "" : $"=== USER GUIDANCE ===\n{request.UserGuidance}\n")}

=== SOURCE STEP DEFINITION ({request.SourceStepDefinitionFile}) ===
{request.SourceStepDefinitionCode}

=== REQUIRED OUTPUT FORMAT ===
You MUST return your answer strictly in the following format so our automated parser can write both files cleanly:

=== PAGE_OBJECT: Pages/{targetFileName} ===
```csharp
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace PlaywrightMcpHarness.Tests.Pages;

public class {targetClassName}
{{
    // Page Object Implementation
}}
```

=== STEP_DEFINITION: {request.SourceStepDefinitionFile} ===
```csharp
using System.Threading.Tasks;
using TechTalk.SpecFlow;
using PlaywrightMcpHarness.Tests.Pages;

namespace PlaywrightMcpHarness.Tests.StepDefinitions;

[Binding]
public class {Path.GetFileNameWithoutExtension(request.SourceStepDefinitionFile)}
{{
    // Refactored Steps injecting {targetClassName}
}}
```

=== EXPLANATION ===
<2-3 sentences explaining the encapsulation and maintainability benefits of this refactor>";

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, "You are an automated C# SpecFlow & Playwright Page Object Model refactoring engine. Adhere strictly to the requested section format."),
                new(ChatRole.User, prompt)
            };

            var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var rawText = response.Text ?? "";

            var pageObjectCode = ExtractSectionCode(rawText, "PAGE_OBJECT");
            var stepDefCode = ExtractSectionCode(rawText, "STEP_DEFINITION");
            var explanation = ExtractExplanation(rawText);

            if (string.IsNullOrWhiteSpace(pageObjectCode) || string.IsNullOrWhiteSpace(stepDefCode))
            {
                return new ExtractPageObjectResponse
                {
                    Success = false,
                    ErrorMessage = "Failed to parse generated Page Object and Step Definition from AI response. Please try again."
                };
            }

            return new ExtractPageObjectResponse
            {
                Success = true,
                PageObjectClassName = targetClassName,
                PageObjectFileName = targetFileName,
                GeneratedPageObjectCode = pageObjectCode,
                RefactoredStepDefinitionCode = stepDefCode,
                Explanation = string.IsNullOrWhiteSpace(explanation)
                    ? $"Successfully encapsulated locators into {targetClassName} with SpecFlow dependency injection."
                    : explanation
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract Page Object from step definitions.");
            return new ExtractPageObjectResponse
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<string> ApplyPageObjectExtractionAsync(
        string stepFileRelativePath,
        string pageObjectFileName,
        string pageObjectCode,
        string refactoredStepsCode)
    {
        var pagesDir = GetPagesDirectory();
        if (!Directory.Exists(pagesDir))
        {
            Directory.CreateDirectory(pagesDir);
        }

        var pageObjectFullPath = _config.ResolveSafePath(Path.GetFileName(pageObjectFileName), "Pages");
        await File.WriteAllTextAsync(pageObjectFullPath, pageObjectCode, Encoding.UTF8);

        var stepsFullPath = _config.ResolveSafePath(stepFileRelativePath);
        if (!File.Exists(stepsFullPath))
        {
            var altPath = _config.ResolveSafePath(Path.GetFileName(stepFileRelativePath), "StepDefinitions");
            if (File.Exists(altPath))
            {
                stepsFullPath = altPath;
            }
        }

        await File.WriteAllTextAsync(stepsFullPath, refactoredStepsCode, Encoding.UTF8);

        await GetPageObjectsAsync(forceReload: true);
        return $"Created Pages/{Path.GetFileName(pageObjectFileName)} and refactored {Path.GetFileName(stepsFullPath)}.";
    }

    private static PageObjectInfo ParsePageObjectFromSource(string content, string fileName, string relPath)
    {
        var po = new PageObjectInfo
        {
            FileName = fileName,
            RelativePath = relPath,
            Content = content
        };

        var classMatch = Regex.Match(content, @"public\s+class\s+(\w+)");
        po.ClassName = classMatch.Success ? classMatch.Groups[1].Value : Path.GetFileNameWithoutExtension(fileName);

        // Extract public methods
        var methodMatches = Regex.Matches(content, @"public\s+(?:async\s+)?(Task<[\w\s,]+>|Task|void)\s+(\w+)\s*\(([^)]*)\)");
        foreach (Match m in methodMatches)
        {
            var returnType = m.Groups[1].Value.Trim();
            var methodName = m.Groups[2].Value.Trim();
            var paramStr = m.Groups[3].Value.Trim();

            var parameters = string.IsNullOrWhiteSpace(paramStr)
                ? new List<string>()
                : paramStr.Split(',').Select(p => p.Trim()).ToList();

            po.Methods.Add(new PageObjectMethodInfo
            {
                MethodName = methodName,
                ReturnType = returnType,
                Signature = $"{methodName}({paramStr})",
                Parameters = parameters
            });
        }

        // Extract locators
        var locatorMatches = Regex.Matches(content, @"(?:_page|Page|[\w]+)\.(GetByRole|GetByLabel|GetByText|GetByPlaceholder|GetByTestId|Locator)\([^;]+");
        foreach (Match lm in locatorMatches)
        {
            var expr = lm.Value.Trim();
            if (!po.Locators.Contains(expr))
            {
                po.Locators.Add(expr);
            }
        }

        return po;
    }

    private static string ExtractSectionCode(string rawText, string sectionMarker)
    {
        var headerPattern = $@"===\s*{sectionMarker}[^=]*===\s*```(?:csharp)?\s*([\s\S]*?)```";
        var match = Regex.Match(rawText, headerPattern, RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        // Alternative pattern: without code fences
        var altPattern = $@"===\s*{sectionMarker}[^=]*===\s*([\s\S]*?)(?:===\s*[A-Z_]+|$)";
        var altMatch = Regex.Match(rawText, altPattern, RegexOptions.IgnoreCase);
        if (altMatch.Success)
        {
            var code = altMatch.Groups[1].Value.Trim();
            if (code.StartsWith("```csharp", StringComparison.OrdinalIgnoreCase))
            {
                code = Regex.Replace(code, @"^```(?:csharp)?\s*", "", RegexOptions.IgnoreCase);
                code = Regex.Replace(code, @"\s*```$", "");
            }
            return code.Trim();
        }

        return "";
    }

    private static string ExtractExplanation(string rawText)
    {
        var pattern = @"===\s*EXPLANATION\s*===\s*([\s\S]*?)$";
        var match = Regex.Match(rawText, pattern, RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }
        return "";
    }

    private string GetPagesDirectory()
    {
        return Path.Combine(GetTestsProjectRoot(), "Pages");
    }

    private string GetTestsProjectRoot() => _config.ResolveTestsProjectRoot();
}
