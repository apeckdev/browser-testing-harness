using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
    public string FileType { get; set; } = ""; // "Feature" or "StepDefinition"
    public string Content { get; set; } = "";
}

public interface IMcpAgentService : IAsyncDisposable
{
    event Action<string>? OnStatusUpdate;
    event Action<string>? OnAgentMessageChunk;
    event Action<string>? OnFileSaved;

    IReadOnlyList<ChatMessage> ConversationHistory { get; }
    Task ResetConversationAsync();

    Task<string> ExecutePromptAsync(
        string userPrompt,
        string? providerKey = null,
        LlmProviderConfig? customOverride = null,
        CancellationToken cancellationToken = default);

    Task<List<TestFileInfo>> GetExistingTestFilesAsync();
    Task<string> SaveFileDirectAsync(string relativePath, string content);
    Task<bool> DeleteTestFileAsync(string relativePath);
}

public class McpAgentService : IMcpAgentService
{
    private readonly HarnessConfig _config;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly ILogger<McpAgentService> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private McpClient? _mcpClient;
    private readonly List<ChatMessage> _conversationHistory = new();

    public event Action<string>? OnStatusUpdate;
    public event Action<string>? OnAgentMessageChunk;
    public event Action<string>? OnFileSaved;

    public IReadOnlyList<ChatMessage> ConversationHistory => _conversationHistory;

    public McpAgentService(
        IOptions<HarnessConfig> configOptions,
        IChatClientFactory chatClientFactory,
        ILogger<McpAgentService> logger)
    {
        _config = configOptions.Value;
        _chatClientFactory = chatClientFactory;
        _logger = logger;

        InitializeSystemPrompt();
    }

    private void InitializeSystemPrompt()
    {
        _conversationHistory.Clear();
        var systemPrompt = @"You are a Principal .NET QA Automation Architect specializing in Playwright and SpecFlow test authoring.
You build reliable, production-grade test suites in the PlaywrightMcpHarness.Tests project.

CORE OPERATIONAL RULES:
1. EXPLORATION FIRST:
   - When asked to create or update tests for a web application, ALWAYS use the Playwright MCP browser tools (e.g., browser_navigate, browser_snapshot, browser_click, browser_fill, etc.) to explore the target URL live in the visible Chromium browser.
   - Inspect the accessibility snapshot carefully to discover robust, resilient locators and exact text/titles.

2. LOCATOR RESILIENCE & PLAYWRIGHT STRICT MODE:
   - Prefer user-facing role locators: GetByRole(AriaRole.Button, new() { Name = '...' }), GetByRole(AriaRole.Textbox), etc.
   - Avoid brittle CSS selectors or absolute XPath expressions.
   - When targeting links or buttons that may appear both in header navigation and footer areas, scope them or use '.First' (e.g. GetByRole(AriaRole.Link, new() { Name = ... }).First) to avoid Playwright strict mode violations.

3. INSPECT EXISTING CODE:
   - Call the local tool 'ReadExistingTestFiles' before creating new tests so you follow existing naming conventions, namespaces, and step definition patterns.

4. SPECFLOW FEATURE AUTHORING:
   - Call the local tool 'SaveFeatureFile' with a descriptive file name (e.g. 'Todo.feature', 'Cart.feature') and valid Gherkin syntax:
     Feature: ...
       Scenario: ...
         Given ...
         When ...
         Then ...
   - Saving a feature file automatically triggers dotnet build to compile and generate SpecFlow code-behind.

5. C# STEP DEFINITION AUTHORING:
   - Call the local tool 'SaveStepDefinitionFile' with a matching C# filename (e.g. 'TodoSteps.cs', 'CartSteps.cs').
   - Use 'TechTalk.SpecFlow' and 'Microsoft.Playwright'.
   - Decorate class with [Binding].
   - Constructor-inject 'IPage page'.
   - Use 'await Assertions.Expect(locator)...' for clean assertions.
   - Use async Task for all step methods.
   - IMPORTANT: DO NOT invent non-existent types or exceptions like 'ScenarioContext.ScenarioContextInjectionException'. Always use standard .NET exceptions such as 'System.ArgumentException' or 'System.NotSupportedException' if a switch case is unhandled.

6. FINAL SUMMARY:
   - Conclude your answer with a clear summary of the steps created, the resilient locators chosen, and confirmation that tests are ready for visual execution in the Studio.";

        _conversationHistory.Add(new ChatMessage(ChatRole.System, systemPrompt));
    }

    public Task ResetConversationAsync()
    {
        InitializeSystemPrompt();
        OnStatusUpdate?.Invoke("Agent conversation reset.");
        return Task.CompletedTask;
    }

    private string GetTestsProjectRoot()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _config.TestsProjectPath));
        if (!Directory.Exists(path) && Directory.Exists(Path.GetFullPath(_config.TestsProjectPath)))
        {
            path = Path.GetFullPath(_config.TestsProjectPath);
        }
        return path;
    }

    private string[] GetNormalizedMcpArguments()
    {
        var rawArgs = _config.PlaywrightMcp.Arguments;
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

    private async Task EnsureMcpClientConnectedAsync(CancellationToken cancellationToken)
    {
        if (_mcpClient != null)
        {
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_mcpClient != null)
            {
                return;
            }

            OnStatusUpdate?.Invoke("Connecting to Playwright MCP server...");

            var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["PLAYWRIGHT_MCP_PING_TIMEOUT_MS"] = "0"
            };

            var command = _config.PlaywrightMcp.Command;
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

            var normalizedArgs = GetNormalizedMcpArguments();
            _logger.LogInformation("Launching Playwright MCP server with {Command} {Args}", command, string.Join(" ", normalizedArgs));

            var transportOptions = new StdioClientTransportOptions
            {
                Command = command,
                Arguments = normalizedArgs,
                EnvironmentVariables = env
            };

            var transport = new StdioClientTransport(transportOptions);
            _mcpClient = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);

            OnStatusUpdate?.Invoke("Playwright MCP server connected (headed Chromium browser ready).");
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

        return result;
    }

    public async Task<string> SaveFileDirectAsync(string relativePath, string content)
    {
        var root = GetTestsProjectRoot();
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8);

        if (relativePath.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
        {
            await RunDotnetBuildAsync(root);
        }

        OnFileSaved?.Invoke(relativePath);
        OnStatusUpdate?.Invoke($"Updated file: {relativePath}");
        return $"Saved {relativePath} successfully.";
    }

    public async Task<bool> DeleteTestFileAsync(string relativePath)
    {
        var root = GetTestsProjectRoot();
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
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

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            return proc.ExitCode == 0
                ? "SpecFlow build succeeded (code-behind generated)."
                : $"SpecFlow build output (code {proc.ExitCode}): {stdout} {stderr}";
        }
        catch (Exception ex)
        {
            return $"Dotnet build error: {ex.Message}";
        }
    }

    private string ToolReadExistingTestFiles()
    {
        OnStatusUpdate?.Invoke("Executing Tool: ReadExistingTestFiles...");
        var root = GetTestsProjectRoot();
        var sb = new StringBuilder();

        var featuresDir = Path.Combine(root, "Features");
        if (Directory.Exists(featuresDir))
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
        if (Directory.Exists(stepsDir))
        {
            foreach (var file in Directory.GetFiles(stepsDir, "*.cs", SearchOption.AllDirectories))
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

    private async Task<string> ToolSaveFeatureFileAsync(string fileName, string gherkinContent)
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

    private async Task<string> ToolSaveStepDefinitionFileAsync(string fileName, string csharpCode)
    {
        OnStatusUpdate?.Invoke($"Executing Tool: SaveStepDefinitionFile ({fileName})...");
        if (!fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".cs";
        }

        var safeName = Path.GetFileName(fileName);
        var root = GetTestsProjectRoot();
        var stepsDir = Path.Combine(root, "StepDefinitions");
        if (!Directory.Exists(stepsDir))
        {
            Directory.CreateDirectory(stepsDir);
        }

        var filePath = Path.Combine(stepsDir, safeName);
        await File.WriteAllTextAsync(filePath, csharpCode, Encoding.UTF8);

        OnStatusUpdate?.Invoke($"Saved Step Definition: {safeName}");

        var relPath = Path.Combine("StepDefinitions", safeName);
        OnFileSaved?.Invoke(relPath);

        return $"Successfully wrote {safeName} into StepDefinitions/ directory.";
    }

    public async Task<string> ExecutePromptAsync(
        string userPrompt,
        string? providerKey = null,
        LlmProviderConfig? customOverride = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureMcpClientConnectedAsync(cancellationToken);

        var activeProviderName = providerKey ?? _config.ActiveProvider;
        OnStatusUpdate?.Invoke($"Preparing tools and querying {activeProviderName}...");

        // Retrieve tools from MCP client
        var mcpTools = await _mcpClient!.ListToolsAsync(options: null, cancellationToken: cancellationToken);

        var allTools = new List<AITool>();

        // Wrap MCP tools with status notifications
        foreach (var mcpTool in mcpTools)
        {
            allTools.Add(new ObservableAIFunction(mcpTool, name => OnStatusUpdate?.Invoke($"Executing MCP Tool: {name}...")));
        }

        // Add 3 custom local file-system tools
        var readTool = AIFunctionFactory.Create(
            ToolReadExistingTestFiles,
            "ReadExistingTestFiles",
            "Lists and returns the contents of all .feature and *Steps.cs files in the PlaywrightMcpHarness.Tests project.");

        var saveFeatureTool = AIFunctionFactory.Create(
            ToolSaveFeatureFileAsync,
            "SaveFeatureFile",
            "Writes/updates a .feature file in PlaywrightMcpHarness.Tests/Features and triggers dotnet build to regenerate SpecFlow code-behind.");

        var saveStepTool = AIFunctionFactory.Create(
            ToolSaveStepDefinitionFileAsync,
            "SaveStepDefinitionFile",
            "Writes/updates a *Steps.cs file in PlaywrightMcpHarness.Tests/StepDefinitions.");

        allTools.Add(new ObservableAIFunction(readTool, name => OnStatusUpdate?.Invoke($"Executing Local Tool: {name}...")));
        allTools.Add(new ObservableAIFunction(saveFeatureTool, name => OnStatusUpdate?.Invoke($"Executing Local Tool: {name}...")));
        allTools.Add(new ObservableAIFunction(saveStepTool, name => OnStatusUpdate?.Invoke($"Executing Local Tool: {name}...")));

        _conversationHistory.Add(new ChatMessage(ChatRole.User, userPrompt));

        var chatClient = _chatClientFactory.CreateClient(providerKey, customOverride);

        var chatOptions = new ChatOptions
        {
            Tools = allTools
        };

        OnStatusUpdate?.Invoke($"Waiting for {activeProviderName} response...");

        var fullResponseBuilder = new StringBuilder();

        try
        {
            await foreach (var update in chatClient.GetStreamingResponseAsync(_conversationHistory, chatOptions, cancellationToken))
            {
                var text = update.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    fullResponseBuilder.Append(text);
                    OnAgentMessageChunk?.Invoke(text);
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

    public ObservableAIFunction(AIFunction innerFunction, Action<string> onExecuting)
        : base(innerFunction)
    {
        _onExecuting = onExecuting;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        _onExecuting(Name);
        return await base.InvokeCoreAsync(arguments, cancellationToken);
    }
}
