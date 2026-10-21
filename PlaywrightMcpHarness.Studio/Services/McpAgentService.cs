using System;
using System.Collections.Generic;
using System.ComponentModel;
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
using ModelContextProtocol;
using ModelContextProtocol.Client;
using PlaywrightMcpHarness.Studio.Models;

namespace PlaywrightMcpHarness.Studio.Services;

public class TestFileInfo
{
    public string RelativePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FileType { get; set; } = ""; // "Feature", "StepDefinition", or "PageObject"
    public string Content { get; set; } = "";
}

public interface IMcpAgentService : IAsyncDisposable
{
    event Action<string>? OnStatusUpdate;
    event Action<string>? OnAgentMessageChunk;
    event Action<string>? OnFileSaved;

    IReadOnlyList<ChatMessage> ConversationHistory { get; }
    Task ResetConversationAsync();
    Task DisconnectMcpAsync();

    Task<string> ExecutePromptAsync(
        string userPrompt,
        string? providerKey = null,
        LlmProviderConfig? customOverride = null,
        PlaywrightMcpConfig? playwrightMcpOverride = null,
        CancellationToken cancellationToken = default);

    Task<List<TestFileInfo>> GetExistingTestFilesAsync();
    Task<string> SaveFileDirectAsync(string relativePath, string content);
    Task<bool> DeleteTestFileAsync(string relativePath);
}

public class McpAgentService : IMcpAgentService
{
    private readonly HarnessConfig _config;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly IStepCatalogService _stepCatalog;
    private readonly ILogger<McpAgentService> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private McpClient? _mcpClient;
    private string? _activeConnectionEndpoint;
    private readonly List<ChatMessage> _conversationHistory = new();

    public event Action<string>? OnStatusUpdate;
    public event Action<string>? OnAgentMessageChunk;
    public event Action<string>? OnFileSaved;

    public IReadOnlyList<ChatMessage> ConversationHistory => _conversationHistory;

    public McpAgentService(
        IOptions<HarnessConfig> configOptions,
        IChatClientFactory chatClientFactory,
        IStepCatalogService stepCatalog,
        ILogger<McpAgentService> logger)
    {
        _config = configOptions.Value;
        _chatClientFactory = chatClientFactory;
        _stepCatalog = stepCatalog;
        _logger = logger;

        InitializeSystemPrompt();
    }

    private string BuildSystemPrompt()
    {
        var stepCatalogSummary = _stepCatalog.GetCatalogSummaryForPrompt();

        return $@"You are a Principal .NET QA Automation Architect specializing in Playwright and SpecFlow test authoring.
You build reliable, production-grade test suites in the PlaywrightMcpHarness.Tests project.

CORE OPERATIONAL RULES:
1. EXPLORATION FIRST:
   - When asked to create or update tests for a web application, ALWAYS use the Playwright MCP browser tools (e.g., browser_navigate, browser_snapshot, browser_click, browser_fill, etc.) to explore the target URL live in the visible Chromium browser.
   - Inspect the accessibility snapshot carefully to discover robust, resilient locators and exact text/titles.

2. LOCATOR RESILIENCE & PLAYWRIGHT STRICT MODE (CRITICAL BEST PRACTICES):
   - ALWAYS prioritize user-facing semantic Playwright locators:
     * Page.GetByRole(AriaRole.<Role>, new() {{ Name = ""..."" }}) for buttons, links, headings, checkboxes, etc.
     * Page.GetByLabel(""..."") for form controls
     * Page.GetByPlaceholder(""..."") for inputs with placeholder text
     * Page.GetByText(""..."") for static labels/headings
     * Page.GetByTestId(""..."") when no accessible user-facing role/label exists
   - NEVER use brittle structural CSS selectors (e.g. ""div > ul > li:nth-child(2) > a""), unadorned bare tags (""button"", ""input""), or deep XPath (""//div/form/div[2]/..."").
   - When targeting links or buttons that may appear both in header navigation and footer areas, scope them or use "".First"" (e.g. GetByRole(AriaRole.Link, new() {{ Name = ... }}).First) to avoid Playwright strict mode violations.

3. STEP DEFINITION REUSE (DEDUPLICATION - CRITICAL):
   - Review the indexed SpecFlow step definitions below before authoring any new C# code.
   - ALWAYS reuse existing steps in your .feature files whenever an interaction or assertion is already implemented.
   - For example, reuse 'Given I am on the ""Home"" page' or 'When I click the ""Submit"" button' rather than generating duplicate C# methods.
   - Only write a new StepDefinition class or method when the test requires an interaction or assertion NOT covered by existing steps.

4. INSPECT EXISTING CODE & PRESERVE SHARED STEPS:
   - Call the local tool 'ReadExistingTestFiles' or 'GetExistingStepCatalog' before creating or updating tests so you follow existing naming conventions, namespaces, and step definition patterns.
   - CRITICAL WHEN UPDATING EXISTING STEP FILES: Because 'SaveStepDefinitionFile' overwrites the entire file, you MUST preserve all existing step methods and all existing switch/if cases (such as page name mappings in 'HomeSteps.cs' used by other .feature files). Never delete existing cases or methods that other scenarios depend on!
   - NEVER guess URLs when adding page mappings (e.g., do not guess 'privacy-policy.html' vs 'privacy.html'). Always verify the exact href/URL using 'ReadExistingTestFiles' or Playwright MCP browser tools ('browser_navigate' / 'browser_snapshot').

5. SPECFLOW FEATURE AUTHORING:
   - Call the local tool 'SaveFeatureFile' with a descriptive file name (e.g. 'Todo.feature', 'Cart.feature') and valid Gherkin syntax:
     Feature: ...
       Scenario: ...
         Given ...
         When ...
         Then ...
   - Saving a feature file automatically triggers dotnet build to compile and generate SpecFlow code-behind.

6. C# STEP DEFINITION AUTHORING (TEST FRAMEWORK IS XUNIT - NEVER NUNIT):
   - Call the local tool 'SaveStepDefinitionFile' with a matching C# filename (e.g. 'TodoSteps.cs', 'CartSteps.cs').
   - CRITICAL - ALWAYS CALL THE SAVE TOOL (NO RAW CODE DUMPS IN CHAT): Never paste full C# or Gherkin files inside markdown code blocks (```csharp or ```gherkin) in your chat message! Always persist changes by calling 'SaveStepDefinitionFile', 'SaveFeatureFile', or 'SavePageObjectFile' directly.
   - Use 'TechTalk.SpecFlow' and 'Microsoft.Playwright'.
   - Decorate class with [Binding].
   - Constructor-inject 'IPage page'.
   - Use 'await Assertions.Expect(locator)...' for clean assertions.
   - Use async Task for all step methods.
   - CRITICAL - SPECFLOW REGEX SYNTAX & JSON ESCAPING: SpecFlow v3.9 step attributes ([Given(...)], [When(...)], [Then(...)]) use Regular Expressions, NOT {{paramName}} route templates! Use regex capture groups such as (.*) for strings and ([0-9.]+) or (\d+) for numbers. Prefer standard C# strings with backslash-escaped quotes rather than verbatim @""...""""..."" strings so your JSON tool call arguments are never malformed:
     * CORRECT: [Given(""I am on the \""(.*)\"" page"")]
     * CORRECT: [Then(""I should see a link named \""(.*)\"" with URL \""(.*)\"""")]
     * WRONG (breaks SpecFlow at runtime with XUnitPendingStepException): [Given(""I am on the \""{{pageName}}\"" page"")]
   - CRITICAL - NO NUNIT: The project uses 'xunit' and 'SpecFlow.xUnit'. DO NOT use NUnit! DO NOT import 'using NUnit.Framework;', DO NOT use 'Assert.That()', '[Test]', or any NUnit syntax! Use 'Microsoft.Playwright.Assertions.Expect(...)' or 'Xunit.Assert'. Any use of NUnit will fail compilation because NUnit is NOT installed.
   - IMPORTANT: DO NOT invent non-existent types or exceptions like 'ScenarioContext.ScenarioContextInjectionException'. Always use standard .NET exceptions such as 'System.ArgumentException' or 'System.NotSupportedException' if a switch case is unhandled.

7. FINAL SUMMARY:
   - Conclude your answer with a clear summary of the steps created, which existing steps were reused, and confirmation that tests are ready for visual execution in the Studio.
{(_config.UsePageObjectModel ? @"
8. PAGE OBJECT MODEL (POM) ARCHITECTURE ENABLED:
   - Separate UI locators and actions into dedicated Page Object classes in namespace 'PlaywrightMcpHarness.Tests.Pages'.
   - Save Page Objects with 'SavePageObjectFile' (e.g., 'Pages/ContactPage.cs').
   - The Page Object constructor should take 'IPage page'.
   - Step definitions in 'StepDefinitions/' should inject the Page Object (e.g. 'public ContactSteps(ContactPage contactPage)') and delegate actions to it.
   - SpecFlow's BoDi DI container will automatically instantiate and inject the Page Object.
" : "")}
9. VISUAL REGRESSION & GOLDEN BASELINE TESTING:
   - When asked to visually verify an entire page or UI component layout, ALWAYS reuse the built-in visual assertion steps:
     * Then the page appearance should match baseline ""<snapshotName>""
     * Then the page appearance should match baseline ""<snapshotName>"" with tolerance <tolerance>%
     * Then the ""<selector>"" element appearance should match baseline ""<snapshotName>""
     * Then the ""<selector>"" element appearance should match baseline ""<snapshotName>"" with tolerance <tolerance>%
   - CRITICAL: DO NOT CREATE ANY C# STEP DEFINITIONS FOR VISUAL REGRESSION TESTING! VisualSteps.cs is already built-in to the test harness. You only need to write the SpecFlow .feature file and reuse these steps.
   - The harness automatically checks against golden references in VisualBaselines/ and highlights pixel diffs in the Studio.

{stepCatalogSummary}";
    }

    private void InitializeSystemPrompt()
    {
        _conversationHistory.Clear();
        _conversationHistory.Add(new ChatMessage(ChatRole.System, BuildSystemPrompt()));
    }

    private async Task RefreshSystemPromptInHistoryAsync()
    {
        await _stepCatalog.ScanStepDefinitionsAsync();
        var latestSystemPrompt = new ChatMessage(ChatRole.System, BuildSystemPrompt());
        if (_conversationHistory.Count > 0 && _conversationHistory[0].Role == ChatRole.System)
        {
            _conversationHistory[0] = latestSystemPrompt;
        }
        else
        {
            _conversationHistory.Insert(0, latestSystemPrompt);
        }
    }

    public async Task ResetConversationAsync()
    {
        await _stepCatalog.ScanStepDefinitionsAsync();
        InitializeSystemPrompt();
        OnStatusUpdate?.Invoke("Agent conversation reset.");
    }

    private string GetTestsProjectRoot() => _config.ResolveTestsProjectRoot();

    private string[] GetNormalizedMcpArguments(PlaywrightMcpConfig mcpConfig)
    {
        var rawArgs = mcpConfig.Arguments;
        if (rawArgs == null || rawArgs.Count == 0)
        {
            return new[] { "-y", "@playwright/mcp@latest", "--browser", "chromium", "--timeout-action", "60000" };
        }

        var packageArg = rawArgs.FirstOrDefault(a => a.Contains("@playwright/mcp")) ?? "@playwright/mcp@latest";
        var normalized = new List<string> { "-y", packageArg };

        for (int i = 0; i < rawArgs.Count; i++)
        {
            var arg = rawArgs[i];
            if (arg == "-y" || arg.Contains("@playwright/mcp"))
            {
                continue;
            }

            if (arg.StartsWith("--"))
            {
                if (!normalized.Contains(arg))
                {
                    normalized.Add(arg);
                    if (i + 1 < rawArgs.Count && !rawArgs[i + 1].StartsWith("--"))
                    {
                        normalized.Add(rawArgs[i + 1]);
                        i++;
                    }
                }
            }
            else if (!normalized.Contains(arg))
            {
                normalized.Add(arg);
            }
        }

        if (!normalized.Contains("--browser"))
        {
            normalized.Add("--browser");
            normalized.Add("chromium");
        }

        if (!normalized.Contains("--timeout-action"))
        {
            normalized.Add("--timeout-action");
            normalized.Add("60000");
        }

        return normalized.ToArray();
    }

    public async Task DisconnectMcpAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            if (_mcpClient != null)
            {
                await _mcpClient.DisposeAsync();
                _mcpClient = null;
                _activeConnectionEndpoint = null;
                OnStatusUpdate?.Invoke("Playwright MCP server disconnected.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disconnecting Playwright MCP client.");
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task EnsureMcpClientConnectedAsync(PlaywrightMcpConfig mcpConfig, CancellationToken cancellationToken)
    {
        var targetEndpoint = !string.IsNullOrWhiteSpace(mcpConfig.ServerUrl)
            ? mcpConfig.ServerUrl.Trim()
            : "stdio";

        if (_mcpClient != null && string.Equals(_activeConnectionEndpoint, targetEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_mcpClient != null && string.Equals(_activeConnectionEndpoint, targetEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_mcpClient != null)
            {
                try
                {
                    await _mcpClient.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing previous McpClient.");
                }
                finally
                {
                    _mcpClient = null;
                    _activeConnectionEndpoint = null;
                }
            }

            if (!string.IsNullOrWhiteSpace(mcpConfig.ServerUrl))
            {
                var remoteUrl = mcpConfig.ServerUrl.Trim();
                OnStatusUpdate?.Invoke($"Connecting to remote Playwright MCP server at {remoteUrl}...");
                _logger.LogInformation("Connecting to remote Playwright MCP server at {Url}", remoteUrl);

                var transportOptions = new HttpClientTransportOptions
                {
                    Endpoint = new Uri(remoteUrl)
                };
                var transport = new HttpClientTransport(transportOptions);
                _mcpClient = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
                _activeConnectionEndpoint = targetEndpoint;

                OnStatusUpdate?.Invoke($"Playwright MCP server connected (remote SSE at {remoteUrl}).");
            }
            else
            {
                OnStatusUpdate?.Invoke("Connecting to Playwright MCP server...");

                var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PLAYWRIGHT_MCP_PING_TIMEOUT_MS"] = "0"
                };

                var command = mcpConfig.Command;
                if (OperatingSystem.IsWindows() && string.Equals(command, "npx", StringComparison.OrdinalIgnoreCase))
                {
                    var npxPath = Environment.GetEnvironmentVariable("PATH")?
                        .Split(';')
                        .Select(p => Path.Combine(p.Trim(), "npx.cmd"))
                        .FirstOrDefault(File.Exists);

                    if (!string.IsNullOrEmpty(npxPath))
                    {
                        command = npxPath;
                    }
                }

                var normalizedArgs = GetNormalizedMcpArguments(mcpConfig);
                _logger.LogInformation("Launching Playwright MCP server with {Command} {Args}", command, string.Join(" ", normalizedArgs));

                var transportOptions = new StdioClientTransportOptions
                {
                    Command = command,
                    Arguments = normalizedArgs,
                    EnvironmentVariables = env
                };

                var transport = new StdioClientTransport(transportOptions);
                _mcpClient = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
                _activeConnectionEndpoint = targetEndpoint;

                OnStatusUpdate?.Invoke("Playwright MCP server connected (headed Chromium browser ready).");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Playwright MCP server.");
            OnStatusUpdate?.Invoke($"Playwright MCP connection failed: {ex.Message}");
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<List<TestFileInfo>> GetExistingTestFilesAsync()
    {
        var result = new List<TestFileInfo>();
        var root = GetTestsProjectRoot();
        if (!Directory.Exists(root))
        {
            return result;
        }

        var featuresDir = Path.Combine(root, "Features");
        if (Directory.Exists(featuresDir))
        {
            foreach (var file in Directory.GetFiles(featuresDir, "*.feature", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file);
                var content = await File.ReadAllTextAsync(file);
                result.Add(new TestFileInfo
                {
                    RelativePath = rel,
                    FileName = Path.GetFileName(file),
                    FileType = "Feature",
                    Content = content
                });
            }
        }

        var stepsDir = Path.Combine(root, "StepDefinitions");
        if (Directory.Exists(stepsDir))
        {
            foreach (var file in Directory.GetFiles(stepsDir, "*Steps.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file);
                var content = await File.ReadAllTextAsync(file);
                result.Add(new TestFileInfo
                {
                    RelativePath = rel,
                    FileName = Path.GetFileName(file),
                    FileType = "StepDefinition",
                    Content = content
                });
            }
        }

        var pagesDir = Path.Combine(root, "Pages");
        if (Directory.Exists(pagesDir))
        {
            foreach (var file in Directory.GetFiles(pagesDir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file);
                var content = await File.ReadAllTextAsync(file);
                result.Add(new TestFileInfo
                {
                    RelativePath = rel,
                    FileName = Path.GetFileName(file),
                    FileType = "PageObject",
                    Content = content
                });
            }
        }

        return result;
    }

    public async Task<string> SaveFileDirectAsync(string relativePath, string content)
    {
        var root = GetTestsProjectRoot();
        var fullPath = _config.ResolveSafePath(relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            content = SanitizeSpecFlowStepAttributes(content, Path.GetFileName(fullPath));
        }

        await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8);

        if (relativePath.EndsWith(".feature", StringComparison.OrdinalIgnoreCase) ||
            relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            await RunDotnetBuildAsync(root);
        }

        OnFileSaved?.Invoke(relativePath);
        OnStatusUpdate?.Invoke($"Updated file: {relativePath}");
        return $"Saved {relativePath} successfully.";
    }

    public async Task<bool> DeleteTestFileAsync(string relativePath)
    {
        if (Path.GetFileName(relativePath).Equals("VisualSteps.cs", StringComparison.OrdinalIgnoreCase))
        {
            OnStatusUpdate?.Invoke("Cannot delete built-in harness file VisualSteps.cs.");
            return false;
        }

        var root = GetTestsProjectRoot();
        var fullPath = _config.ResolveSafePath(relativePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);

            // If it's a feature file, also remove the generated code-behind .feature.cs if it exists
            if (relativePath.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
            {
                var csFile = fullPath + ".cs";
                if (File.Exists(csFile))
                {
                    File.Delete(csFile);
                }
            }

            // Always trigger a build to ensure project state and assembly are clean
            await RunDotnetBuildAsync(root);

            OnStatusUpdate?.Invoke($"Deleted file: {relativePath}");
            return true;
        }
        return false;
    }

    private static readonly Regex SpecFlowAttributeLineRegex = new(
        @"(\[\s*(?:Given|When|Then|StepDefinition)\s*\(\s*@?""(?:""""|\\.|[^""])*"")",
        RegexOptions.Compiled);

    private static readonly Regex NamedPlaceholderInStepRegex = new(
        @"\{[a-zA-Z_][a-zA-Z0-9_]*\}",
        RegexOptions.Compiled);

    private string SanitizeSpecFlowStepAttributes(string csharpCode, string fileName)
    {
        bool modified = false;
        var sanitized = SpecFlowAttributeLineRegex.Replace(csharpCode, match =>
        {
            var attrText = match.Value;
            if (NamedPlaceholderInStepRegex.IsMatch(attrText))
            {
                modified = true;
                return NamedPlaceholderInStepRegex.Replace(attrText, "(.*)");
            }
            return attrText;
        });

        if (modified)
        {
            _logger.LogInformation("Converted named {{param}} placeholders to (.*) regex capture groups in SpecFlow attributes of {FileName}", fileName);
        }

        return sanitized;
    }

    private async Task<string> RunDotnetBuildAsync(string projectDir)
    {
        try
        {
            var userDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet.exe");
            var dotnetExe = File.Exists(userDotnet) ? userDotnet : "dotnet";

            var psi = new ProcessStartInfo
            {
                FileName = dotnetExe,
                Arguments = "build PlaywrightMcpHarness.Tests.csproj",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var userDotnetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.EnvironmentVariables["PATH"] = $"{userDotnetDir};{currentPath}";

            using var proc = Process.Start(psi);
            if (proc == null) return "Failed to start dotnet build.";

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync();

            return proc.ExitCode == 0
                ? "SpecFlow build succeeded (code-behind generated)."
                : $"SpecFlow build output (code {proc.ExitCode}): {stdoutTask.Result} {stderrTask.Result}";
        }
        catch (Exception ex)
        {
            return $"Dotnet build error: {ex.Message}";
        }
    }

    private string ToolReadExistingTestFiles(
        [Description("Optional category filter: 'all', 'features', 'steps', or 'pages'. Defaults to 'all'.")] string? filter = "all")
    {
        OnStatusUpdate?.Invoke("Executing Tool: ReadExistingTestFiles...");
        var root = GetTestsProjectRoot();
        var sb = new StringBuilder();
        var normFilter = string.IsNullOrWhiteSpace(filter) ? "all" : filter.Trim().ToLowerInvariant();

        var featuresDir = Path.Combine(root, "Features");
        if ((normFilter is "all" or "features" or "feature") && Directory.Exists(featuresDir))
        {
            foreach (var file in Directory.GetFiles(featuresDir, "*.feature", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file);
                sb.AppendLine($"=== File: {rel} ===");
                sb.AppendLine(File.ReadAllText(file));
                sb.AppendLine();
            }
        }

        var stepsDir = Path.Combine(root, "StepDefinitions");
        if ((normFilter is "all" or "steps" or "stepdefinitions") && Directory.Exists(stepsDir))
        {
            foreach (var file in Directory.GetFiles(stepsDir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file);
                sb.AppendLine($"=== File: {rel} ===");
                sb.AppendLine(File.ReadAllText(file));
                sb.AppendLine();
            }
        }

        var pagesDir = Path.Combine(root, "Pages");
        if ((normFilter is "all" or "pages" or "pageobjects") && Directory.Exists(pagesDir))
        {
            foreach (var file in Directory.GetFiles(pagesDir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file);
                sb.AppendLine($"=== File: {rel} ===");
                sb.AppendLine(File.ReadAllText(file));
                sb.AppendLine();
            }
        }

        var result = sb.ToString();
        return string.IsNullOrWhiteSpace(result) ? "No existing test files found in project." : result;
    }

    private async Task<string> ToolSaveFeatureFileAsync(
        [Description("The file name of the SpecFlow feature file (e.g. 'Navigation.feature').")] string fileName,
        [Description("The complete valid Gherkin content starting with 'Feature:'.")] string gherkinContent)
    {
        OnStatusUpdate?.Invoke($"Executing Tool: SaveFeatureFile ({fileName})...");
        if (!fileName.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".feature";
        }

        var safeName = Path.GetFileName(fileName);
        var root = GetTestsProjectRoot();
        var featuresDir = Path.Combine(root, "Features");
        if (!Directory.Exists(featuresDir))
        {
            Directory.CreateDirectory(featuresDir);
        }

        var filePath = Path.Combine(featuresDir, safeName);
        await File.WriteAllTextAsync(filePath, gherkinContent, Encoding.UTF8);

        OnStatusUpdate?.Invoke($"Saved Feature: {safeName}. Triggering SpecFlow build...");
        var buildReport = await RunDotnetBuildAsync(root);
        OnStatusUpdate?.Invoke($"SpecFlow build completed for {safeName}.");

        var relPath = Path.Combine("Features", safeName);
        OnFileSaved?.Invoke(relPath);

        return $"Successfully wrote {safeName} into Features/ directory. Build Result: {buildReport}";
    }

    private async Task<string> ToolSaveStepDefinitionFileAsync(
        [Description("The file name of the C# step definition file (e.g. 'NavigationSteps.cs').")] string fileName,
        [Description("The complete C# source code for the SpecFlow [Binding] step definition class.")] string csharpCode)
    {
        OnStatusUpdate?.Invoke($"Executing Tool: SaveStepDefinitionFile ({fileName})...");
        if (!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".cs";
        }

        var safeName = Path.GetFileName(fileName);

        // Prevent LLM NUnit hallucination by sanitizing any accidental NUnit imports, attributes, and assertions
        if (csharpCode.Contains("NUnit", StringComparison.OrdinalIgnoreCase) || csharpCode.Contains("Assert.That", StringComparison.OrdinalIgnoreCase))
        {
            csharpCode = Regex.Replace(csharpCode, @"using\s+NUnit\.Framework\s*;", "using Xunit;\nusing Microsoft.Playwright;");
            csharpCode = Regex.Replace(csharpCode, @"using\s+NUnit\s*;", "// NUnit removed - harness uses xUnit");
            csharpCode = Regex.Replace(csharpCode, @"\[TestFixture\]\s*", "");
            csharpCode = Regex.Replace(csharpCode, @"\[Test\]\s*", "");

            // Convert common NUnit Assert.That patterns to xUnit Assert
            csharpCode = Regex.Replace(csharpCode, @"Assert\.That\s*\(\s*(.+?)\s*,\s*Is\.True\s*\);", "Assert.True($1);");
            csharpCode = Regex.Replace(csharpCode, @"Assert\.That\s*\(\s*(.+?)\s*,\s*Is\.False\s*\);", "Assert.False($1);");
            csharpCode = Regex.Replace(csharpCode, @"Assert\.That\s*\(\s*(.+?)\s*,\s*Is\.EqualTo\s*\((.+?)\)\s*\);", "Assert.Equal($2, $1);");
            csharpCode = Regex.Replace(csharpCode, @"Assert\.That\s*\(\s*(.+?)\s*,\s*Is\.Not\.Null\s*\);", "Assert.NotNull($1);");
            csharpCode = Regex.Replace(csharpCode, @"Assert\.That\s*\(\s*(.+?)\s*,\s*Is\.Null\s*\);", "Assert.Null($1);");
            csharpCode = Regex.Replace(csharpCode, @"Assert\.That\s*\(\s*(.+?)\s*,\s*Does\.Contain\s*\((.+?)\)\s*\);", "Assert.Contains($2, $1);");
            csharpCode = Regex.Replace(csharpCode, @"Assert\.That\s*\(\s*(.+?)\s*\);", "Assert.True($1);");

            _logger.LogInformation("Sanitized accidental NUnit reference from generated step definition {FileName}", safeName);
        }

        // Convert any accidental {paramName} placeholders inside [Given]/[When]/[Then] attributes to (.*) regex capture groups
        csharpCode = SanitizeSpecFlowStepAttributes(csharpCode, safeName);

        // Prevent duplicating built-in visual assertion steps
        if (csharpCode.Contains("appearance should match baseline", StringComparison.OrdinalIgnoreCase) && !safeName.Equals("VisualSteps.cs", StringComparison.OrdinalIgnoreCase))
        {
            return $"Skipped saving {safeName}: Visual regression steps are already built-in to the test harness in VisualSteps.cs. Please only author the .feature file referencing 'Then the page appearance should match baseline \"<name>\"'.";
        }

        var root = GetTestsProjectRoot();
        var stepsDir = Path.Combine(root, "StepDefinitions");
        if (!Directory.Exists(stepsDir))
        {
            Directory.CreateDirectory(stepsDir);
        }

        var filePath = Path.Combine(stepsDir, safeName);
        await File.WriteAllTextAsync(filePath, csharpCode, Encoding.UTF8);

        OnStatusUpdate?.Invoke($"Saved Step Definition: {safeName}. Verifying build...");
        var buildReport = await RunDotnetBuildAsync(root);

        var relPath = Path.Combine("StepDefinitions", safeName);
        OnFileSaved?.Invoke(relPath);

        // Re-scan catalog so new steps are immediately indexed
        await _stepCatalog.ScanStepDefinitionsAsync(forceReload: true);

        return $"Successfully wrote {safeName} into StepDefinitions/ directory. Build Result: {buildReport}";
    }

    private async Task<string> ToolSavePageObjectFileAsync(
        [Description("The file name of the C# Page Object class (e.g. 'NavigationPage.cs').")] string fileName,
        [Description("The complete C# source code for the Page Object class in namespace PlaywrightMcpHarness.Tests.Pages.")] string csharpCode)
    {
        OnStatusUpdate?.Invoke($"Executing Tool: SavePageObjectFile ({fileName})...");
        if (!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".cs";
        }

        var safeName = Path.GetFileName(fileName);
        var root = GetTestsProjectRoot();
        var pagesDir = Path.Combine(root, "Pages");
        if (!Directory.Exists(pagesDir))
        {
            Directory.CreateDirectory(pagesDir);
        }

        var filePath = Path.Combine(pagesDir, safeName);
        await File.WriteAllTextAsync(filePath, csharpCode, Encoding.UTF8);

        OnStatusUpdate?.Invoke($"Saved Page Object: {safeName}. Verifying build...");
        var buildReport = await RunDotnetBuildAsync(root);

        var relPath = Path.Combine("Pages", safeName);
        OnFileSaved?.Invoke(relPath);

        return $"Successfully wrote {safeName} into Pages/ directory. Build Result: {buildReport}";
    }

    private async Task<string> ToolGetExistingStepCatalogAsync(
        [Description("Optional step type filter: 'All', 'Given', 'When', or 'Then'. Defaults to 'All'.")] string? stepType = "All")
    {
        OnStatusUpdate?.Invoke("Executing Tool: GetExistingStepCatalog...");
        await _stepCatalog.ScanStepDefinitionsAsync();
        return _stepCatalog.GetCatalogSummaryForPrompt();
    }

    public async Task<string> ExecutePromptAsync(
        string userPrompt,
        string? providerKey = null,
        LlmProviderConfig? customOverride = null,
        PlaywrightMcpConfig? playwrightMcpOverride = null,
        CancellationToken cancellationToken = default)
    {
        await RefreshSystemPromptInHistoryAsync();

        var mcpConfig = playwrightMcpOverride ?? _config.PlaywrightMcp;
        await EnsureMcpClientConnectedAsync(mcpConfig, cancellationToken);

        var activeProviderName = providerKey ?? _config.ActiveProvider;
        OnStatusUpdate?.Invoke($"Preparing tools and querying {activeProviderName}...");

        // Retrieve tools from MCP client
        var mcpTools = await _mcpClient!.ListToolsAsync(options: null, cancellationToken: cancellationToken);

        var allTools = new List<AITool>();

        var executedToolsInTurn = new List<string>();
        var savedFilesInTurn = new List<string>();

        // Wrap MCP tools with status notifications
        foreach (var mcpTool in mcpTools)
        {
            allTools.Add(new ObservableAIFunction(
                mcpTool,
                detail =>
                {
                    executedToolsInTurn.Add(detail);
                    OnStatusUpdate?.Invoke($"Running MCP Tool: {detail}...");
                },
                name => OnStatusUpdate?.Invoke($"Completed MCP Tool: {name}. Awaiting {activeProviderName}...")));
        }

        // Add custom local tools
        var readTool = AIFunctionFactory.Create(
            ToolReadExistingTestFiles,
            "ReadExistingTestFiles",
            "Lists and returns the contents of all .feature and *Steps.cs files in the PlaywrightMcpHarness.Tests project.");

        var catalogTool = AIFunctionFactory.Create(
            ToolGetExistingStepCatalogAsync,
            "GetExistingStepCatalog",
            "Returns the catalog of all currently indexed SpecFlow step definitions with parameters, to help you reuse existing steps.");

        var saveFeatureTool = AIFunctionFactory.Create(
            ToolSaveFeatureFileAsync,
            "SaveFeatureFile",
            "Writes/updates a .feature file in PlaywrightMcpHarness.Tests/Features and triggers dotnet build to regenerate SpecFlow code-behind.");

        var saveStepTool = AIFunctionFactory.Create(
            ToolSaveStepDefinitionFileAsync,
            "SaveStepDefinitionFile",
            "Writes/updates a *Steps.cs file in PlaywrightMcpHarness.Tests/StepDefinitions and verifies compilation.");

        var savePageObjectTool = AIFunctionFactory.Create(
            ToolSavePageObjectFileAsync,
            "SavePageObjectFile",
            "Writes/updates a Page Object class in PlaywrightMcpHarness.Tests/Pages/ and verifies compilation.");

        allTools.Add(new ObservableAIFunction(
            readTool,
            detail =>
            {
                executedToolsInTurn.Add(detail);
                OnStatusUpdate?.Invoke($"Running Local Tool: {detail}...");
            },
            name => OnStatusUpdate?.Invoke($"Completed Local Tool: {name}. Awaiting {activeProviderName}...")));
        allTools.Add(new ObservableAIFunction(
            catalogTool,
            detail =>
            {
                executedToolsInTurn.Add(detail);
                OnStatusUpdate?.Invoke($"Running Local Tool: {detail}...");
            },
            name => OnStatusUpdate?.Invoke($"Completed Local Tool: {name}. Awaiting {activeProviderName}...")));
        allTools.Add(new ObservableAIFunction(
            saveFeatureTool,
            detail =>
            {
                executedToolsInTurn.Add(detail);
                savedFilesInTurn.Add(detail);
                OnStatusUpdate?.Invoke($"Running Local Tool: {detail}...");
            },
            name => OnStatusUpdate?.Invoke($"Completed Local Tool: {name}. Awaiting {activeProviderName}...")));
        allTools.Add(new ObservableAIFunction(
            saveStepTool,
            detail =>
            {
                executedToolsInTurn.Add(detail);
                savedFilesInTurn.Add(detail);
                OnStatusUpdate?.Invoke($"Running Local Tool: {detail}...");
            },
            name => OnStatusUpdate?.Invoke($"Completed Local Tool: {name}. Awaiting {activeProviderName}...")));
        allTools.Add(new ObservableAIFunction(
            savePageObjectTool,
            detail =>
            {
                executedToolsInTurn.Add(detail);
                savedFilesInTurn.Add(detail);
                OnStatusUpdate?.Invoke($"Running Local Tool: {detail}...");
            },
            name => OnStatusUpdate?.Invoke($"Completed Local Tool: {name}. Awaiting {activeProviderName}...")));

        _conversationHistory.Add(new ChatMessage(ChatRole.User, userPrompt));

        var chatClient = _chatClientFactory.CreateClient(providerKey, customOverride);

        var chatOptions = new ChatOptions
        {
            Tools = allTools
        };

        OnStatusUpdate?.Invoke($"Waiting for {activeProviderName} response...");

        var fullResponseBuilder = new StringBuilder();
        var hasStartedStreaming = false;
        const int maxMalformedRetries = 2;
        int malformedAttempt = 0;
        const int maxUnverifiedSaveRetries = 2;
        int unverifiedSaveAttempt = 0;

        try
        {
            while (true)
            {
                var turnResponseBuilder = new StringBuilder();
                try
                {
                    await foreach (var update in chatClient.GetStreamingResponseAsync(_conversationHistory, chatOptions, cancellationToken))
                    {
                        var text = update.Text;
                        if (!string.IsNullOrEmpty(text))
                        {
                            if (!hasStartedStreaming)
                            {
                                hasStartedStreaming = true;
                                OnStatusUpdate?.Invoke($"Generating response from {activeProviderName}...");
                            }
                            turnResponseBuilder.Append(text);
                            fullResponseBuilder.Append(text);
                            OnAgentMessageChunk?.Invoke(text);
                        }
                    }

                    var turnText = turnResponseBuilder.ToString();
                    bool isFixRequest = userPrompt.Contains("The automated test execution encountered a failure", StringComparison.OrdinalIgnoreCase);
                    bool dumpedCodeBlock = turnText.Contains("```csharp", StringComparison.OrdinalIgnoreCase) ||
                                           turnText.Contains("```gherkin", StringComparison.OrdinalIgnoreCase) ||
                                           turnText.Contains("```feature", StringComparison.OrdinalIgnoreCase);
                    bool claimedFileSave = turnText.Contains("has been updated", StringComparison.OrdinalIgnoreCase) ||
                                           turnText.Contains("was modified", StringComparison.OrdinalIgnoreCase) ||
                                           turnText.Contains("were added", StringComparison.OrdinalIgnoreCase) ||
                                           turnText.Contains("successfully compiled", StringComparison.OrdinalIgnoreCase) ||
                                           turnText.Contains("have been resolved", StringComparison.OrdinalIgnoreCase);

                    if (savedFilesInTurn.Count == 0 &&
                        unverifiedSaveAttempt < maxUnverifiedSaveRetries &&
                        (isFixRequest || dumpedCodeBlock || claimedFileSave))
                    {
                        unverifiedSaveAttempt++;
                        _logger.LogWarning("Agent finished turn without calling any Save* tool (attempt {Attempt}/{Max}). Forcing save tool invocation.", unverifiedSaveAttempt, maxUnverifiedSaveRetries);
                        OnStatusUpdate?.Invoke($"Agent did not invoke Save tool. Requesting file save ({unverifiedSaveAttempt}/{maxUnverifiedSaveRetries})...");

                        if (!string.IsNullOrWhiteSpace(turnText))
                        {
                            _conversationHistory.Add(new ChatMessage(ChatRole.Assistant, turnText));
                        }

                        var retryNotice = "\n\n*(Persisting changes to disk...)*\n\n";
                        fullResponseBuilder.Append(retryNotice);
                        OnAgentMessageChunk?.Invoke(retryNotice);

                        _conversationHistory.Add(new ChatMessage(
                            ChatRole.User,
                            "[System Verification Error]: You did NOT call SaveStepDefinitionFile, SaveFeatureFile, or SavePageObjectFile during this turn! Zero files were modified on disk. Do NOT output code blocks in chat or claim files were updated without calling the save tool. Call SaveStepDefinitionFile and/or SaveFeatureFile right now with the complete file content to persist your changes to disk. Remember to use standard C# strings with backslash-escaped quotes like [Given(\"I am on the \\\"(.*)\\\" page\")] instead of verbatim @\"...\"\"...\" strings."));

                        continue;
                    }

                    break;
                }
                catch (ArgumentOutOfRangeException ex) when (
                    ex.Message.Contains("ChatFinishReason", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("MALFORMED_FUNCTION_CALL", StringComparison.OrdinalIgnoreCase))
                {
                    malformedAttempt++;
                    _logger.LogWarning(ex, "Provider returned non-standard/malformed function call finish reason (attempt {Attempt}/{Max}).", malformedAttempt, maxMalformedRetries);

                    if (malformedAttempt <= maxMalformedRetries)
                    {
                        var savedSummary = savedFilesInTurn.Count > 0
                            ? $"Files saved so far in this turn: {string.Join(", ", savedFilesInTurn)}."
                            : "NO files have been saved to disk yet in this turn!";

                        OnStatusUpdate?.Invoke($"Model emitted a malformed tool call. Retrying automatically ({malformedAttempt}/{maxMalformedRetries})...");

                        var retryNotice = "\n\n*(Retrying tool call...)*\n\n";
                        fullResponseBuilder.Append(retryNotice);
                        OnAgentMessageChunk?.Invoke(retryNotice);

                        _conversationHistory.Add(new ChatMessage(
                            ChatRole.User,
                            $"[System Recovery]: Your last function/tool call failed with MALFORMED_FUNCTION_CALL and WAS NOT EXECUTED. {savedSummary} You MUST call SaveStepDefinitionFile / SaveFeatureFile now to save your changes to disk. To avoid MALFORMED_FUNCTION_CALL: (1) Do NOT output markdown code blocks (```csharp) in chat, and (2) in C# step attributes, use standard C# strings with backslash-escaped quotes like [Given(\"I am on the \\\"(.*)\\\" page\")] instead of verbatim @\"...\"\"...\" strings."));

                        continue;
                    }

                    if (executedToolsInTurn.Count > 0 || fullResponseBuilder.Length > 0)
                    {
                        var fallbackMsg = $"\n\n*(Note: The model ended with a malformed function call after executing: {string.Join(", ", executedToolsInTurn)}.)*";
                        fullResponseBuilder.Append(fallbackMsg);
                        OnAgentMessageChunk?.Invoke(fallbackMsg);
                        break;
                    }

                    throw new InvalidOperationException(
                        "The AI model generated a malformed function call (MALFORMED_FUNCTION_CALL) and could not recover after retrying. Try rephrasing your prompt or switching models.",
                        ex);
                }
            }

            var finalContent = fullResponseBuilder.ToString();
            _conversationHistory.Add(new ChatMessage(ChatRole.Assistant, finalContent));
            OnStatusUpdate?.Invoke("Agent completed task.");
            return finalContent;
        }
        catch (OperationCanceledException)
        {
            OnStatusUpdate?.Invoke("Agent execution was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during agent execution.");
            OnStatusUpdate?.Invoke($"Agent Error: {ex.Message}");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_mcpClient != null)
        {
            try
            {
                await _mcpClient.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing McpClient.");
            }
            finally
            {
                _mcpClient = null;
            }
        }

        _connectionLock.Dispose();
    }
}

public class ObservableAIFunction : DelegatingAIFunction
{
    private readonly Action<string> _onExecuting;
    private readonly Action<string>? _onCompleted;

    public ObservableAIFunction(AIFunction innerFunction, Action<string> onExecuting, Action<string>? onCompleted = null)
        : base(innerFunction)
    {
        _onExecuting = onExecuting;
        _onCompleted = onCompleted;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var detail = FormatDetail(arguments);
        _onExecuting(detail);
        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken);
            _onCompleted?.Invoke(Name);
            return result;
        }
        catch (Exception ex)
        {
            _onCompleted?.Invoke($"{Name} (failed: {ex.Message})");
            throw;
        }
    }

    private string FormatDetail(AIFunctionArguments? arguments)
    {
        if (arguments == null || arguments.Count == 0)
        {
            return Name;
        }

        if (arguments.TryGetValue("url", out var url) && url != null)
        {
            return $"{Name} ({url})";
        }
        if (arguments.TryGetValue("fileName", out var fn) && fn != null)
        {
            return $"{Name} ({fn})";
        }
        if (arguments.TryGetValue("selector", out var sel) && sel != null)
        {
            return $"{Name} ({sel})";
        }
        if (arguments.TryGetValue("text", out var txt) && txt != null)
        {
            var truncated = txt.ToString() ?? "";
            if (truncated.Length > 28) truncated = truncated.Substring(0, 28) + "...";
            return $"{Name} (\"{truncated}\")";
        }

        return Name;
    }
}
