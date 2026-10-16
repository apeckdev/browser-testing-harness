using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlaywrightMcpHarness.Studio.Models;

namespace PlaywrightMcpHarness.Studio.Services;

public interface ITestRunnerService
{
    bool IsRunning { get; }
    Task<int> RunTestsAsync(TestRunOptions options, Action<string> onOutputLine, CancellationToken cancellationToken = default);
    Task<int> RunTestsAsync(VisualExecutionMode mode, Action<string> onOutputLine, CancellationToken cancellationToken = default);
    void OpenTraceViewer(string traceFileName);
    string GetTestArtifactsPath();
}

public class TestRunnerService : ITestRunnerService
{
    private readonly HarnessConfig _config;
    private readonly ILogger<TestRunnerService> _logger;
    private bool _isRunning;

    public bool IsRunning => _isRunning;

    public TestRunnerService(IOptions<HarnessConfig> configOptions, ILogger<TestRunnerService> logger)
    {
        _config = configOptions.Value;
        _logger = logger;
    }

    public Task<int> RunTestsAsync(VisualExecutionMode mode, Action<string> onOutputLine, CancellationToken cancellationToken = default)
    {
        return RunTestsAsync(new TestRunOptions { Mode = mode }, onOutputLine, cancellationToken);
    }

    public async Task<int> RunTestsAsync(TestRunOptions options, Action<string> onOutputLine, CancellationToken cancellationToken = default)
    {
        if (_isRunning)
        {
            onOutputLine("[Runner] A test run is already in progress.");
            return -1;
        }

        _isRunning = true;
        try
        {
            var userDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet.exe");
            var dotnetExe = File.Exists(userDotnet) ? userDotnet : "dotnet";

            var testsProjectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _config.TestsProjectPath));
            if (!Directory.Exists(testsProjectDir) && Directory.Exists(Path.GetFullPath(_config.TestsProjectPath)))
            {
                testsProjectDir = Path.GetFullPath(_config.TestsProjectPath);
            }

            var csprojFile = Path.Combine(testsProjectDir, "PlaywrightMcpHarness.Tests.csproj");
            var targetPath = File.Exists(csprojFile) ? csprojFile : testsProjectDir;

            onOutputLine($"[Runner] Target project: {targetPath}");

            var startInfo = new ProcessStartInfo
            {
                FileName = dotnetExe,
                WorkingDirectory = testsProjectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Set PATH to include ~/.dotnet
            var userDotnetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            startInfo.EnvironmentVariables["PATH"] = $"{userDotnetDir};{currentPath}";

            var artifactsDir = Path.Combine(testsProjectDir, "TestArtifacts");
            if (!Directory.Exists(artifactsDir))
            {
                Directory.CreateDirectory(artifactsDir);
            }
            startInfo.EnvironmentVariables["TEST_ARTIFACTS_DIR"] = artifactsDir;
            startInfo.EnvironmentVariables["VISUAL_TOLERANCE_PERCENT"] = _config.VisualTolerancePercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var baselinesDir = Path.Combine(testsProjectDir, "VisualBaselines");
            if (!Directory.Exists(baselinesDir))
            {
                Directory.CreateDirectory(baselinesDir);
            }
            startInfo.EnvironmentVariables["VISUAL_BASELINES_DIR"] = baselinesDir;

            string envArgs;
            switch (options.Mode)
            {
                case VisualExecutionMode.HeadedSlowMo:
                    envArgs = "-e HEADED=1 -e SLOWMO=1000 -e PWDEBUG=0";
                    startInfo.EnvironmentVariables["PWDEBUG"] = "0";
                    startInfo.EnvironmentVariables["HEADED"] = "1";
                    startInfo.EnvironmentVariables["SLOWMO"] = "1000";
                    onOutputLine("[Runner] Visual Mode: Visible Browser (Slow-Motion 1000ms delay per step)");
                    break;
                case VisualExecutionMode.HeadedNormal:
                    envArgs = "-e HEADED=1 -e SLOWMO=0 -e PWDEBUG=0";
                    startInfo.EnvironmentVariables["PWDEBUG"] = "0";
                    startInfo.EnvironmentVariables["HEADED"] = "1";
                    startInfo.EnvironmentVariables["SLOWMO"] = "0";
                    onOutputLine("[Runner] Visual Mode: Visible Browser (Full Speed)");
                    break;
                case VisualExecutionMode.Inspector:
                    envArgs = "-e HEADED=1 -e SLOWMO=0 -e PWDEBUG=1";
                    startInfo.EnvironmentVariables["PWDEBUG"] = "1";
                    startInfo.EnvironmentVariables["HEADED"] = "1";
                    startInfo.EnvironmentVariables["SLOWMO"] = "0";
                    onOutputLine("[Runner] Visual Mode: Interactive Step Debugger (Playwright Inspector)");
                    onOutputLine("[Runner] NOTE: Playwright pauses on each step. Click 'Resume' (F5) or 'Step' (F10) in the Playwright Inspector GUI window to proceed.");
                    break;
                case VisualExecutionMode.Headless:
                default:
                    envArgs = "-e HEADED=0 -e SLOWMO=0 -e PWDEBUG=0";
                    startInfo.EnvironmentVariables["PWDEBUG"] = "0";
                    startInfo.EnvironmentVariables["HEADED"] = "0";
                    startInfo.EnvironmentVariables["SLOWMO"] = "0";
                    onOutputLine("[Runner] Visual Mode: Headless (Fast background execution)");
                    break;
            }

            // Concurrency handling: Sequential (1 browser) vs Parallel
            string runsettingsArg = "";
            if (!options.RunInParallel)
            {
                var runsettingsPath = Path.Combine(testsProjectDir, "sequential.runsettings");
                if (!File.Exists(runsettingsPath))
                {
                    var runsettingsXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<RunSettings>
  <RunConfiguration>
    <MaxCpuCount>1</MaxCpuCount>
    <DisableParallelization>true</DisableParallelization>
  </RunConfiguration>
  <xUnit>
    <ParallelizeTestCollections>false</ParallelizeTestCollections>
    <ParallelizeAssembly>false</ParallelizeAssembly>
  </xUnit>
</RunSettings>";
                    File.WriteAllText(runsettingsPath, runsettingsXml);
                }
                runsettingsArg = $"-s \"{runsettingsPath}\"";
                onOutputLine("[Runner] Concurrency: Sequential (Single browser window at a time)");
            }
            else
            {
                onOutputLine("[Runner] Concurrency: Parallel (Multiple concurrent test collections / windows)");
            }

            // Target filtering
            string filterArg = "";
            if (string.Equals(options.TargetScope, "selected", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(options.SelectedFilePath))
            {
                var filter = ResolveTestFilter(testsProjectDir, options.SelectedFilePath);
                if (!string.IsNullOrEmpty(filter))
                {
                    onOutputLine($"[Runner] Scope: Selected Feature ({filter})");
                    filterArg = $"--filter \"FullyQualifiedName~{filter}\"";
                }
                else
                {
                    onOutputLine("[Runner] Scope: All Features");
                }
            }
            else
            {
                onOutputLine("[Runner] Scope: All Features");
            }

            var extraArgs = string.Join(" ", new[] { runsettingsArg, filterArg, envArgs }.Where(s => !string.IsNullOrWhiteSpace(s)));
            startInfo.Arguments = $"test \"{targetPath}\" {extraArgs} --logger \"console;verbosity=detailed\"";

            using var process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    onOutputLine(e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    onOutputLine($"[ERR] {e.Data}");
                }
            };

            onOutputLine($"[Runner] Spawning: {startInfo.FileName} {startInfo.Arguments}");
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        onOutputLine("[Runner] Cancellation requested. Terminating test process...");
                        process.Kill(entireProcessTree: true);
                    }
                    foreach (var p in Process.GetProcessesByName("testhost"))
                    {
                        try { p.Kill(); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to kill test process on cancellation.");
                }
            });

            await process.WaitForExitAsync(cancellationToken);
            onOutputLine($"[Runner] Test run finished with exit code {process.ExitCode}.");
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            onOutputLine("[Runner] Test execution was cancelled.");
            return -1;
        }
        catch (Exception ex)
        {
            onOutputLine($"[Runner] Execution error: {ex.Message}");
            _logger.LogError(ex, "Failed running tests.");
            return -1;
        }
        finally
        {
            _isRunning = false;
        }
    }

    private string? ResolveTestFilter(string testsDir, string filePath)
    {
        try
        {
            var fileName = Path.GetFileName(filePath);
            string featureBaseName;

            if (fileName.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
            {
                featureBaseName = Path.GetFileNameWithoutExtension(fileName);
            }
            else if (fileName.EndsWith("Steps.cs", StringComparison.OrdinalIgnoreCase))
            {
                featureBaseName = fileName.Substring(0, fileName.Length - "Steps.cs".Length);
            }
            else
            {
                featureBaseName = Path.GetFileNameWithoutExtension(fileName);
            }

            // 1. Check if corresponding .feature.cs exists in Features/
            var csFile = Path.Combine(testsDir, "Features", $"{featureBaseName}.feature.cs");
            if (File.Exists(csFile))
            {
                var content = File.ReadAllText(csFile);
                var match = System.Text.RegularExpressions.Regex.Match(content, @"class\s+(\w+Feature)");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }

            // 2. Check if .feature exists and inspect 'Feature: ...' line
            var featureFile = Path.Combine(testsDir, "Features", $"{featureBaseName}.feature");
            if (File.Exists(featureFile))
            {
                foreach (var line in File.ReadLines(featureFile))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Feature:", StringComparison.OrdinalIgnoreCase))
                    {
                        var title = trimmed.Substring(8).Trim();
                        var identifier = System.Text.RegularExpressions.Regex.Replace(title, @"[^a-zA-Z0-9_]", "");
                        return $"{identifier}Feature";
                    }
                }
            }

            return $"{featureBaseName}Feature";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error resolving test filter for file: {FilePath}", filePath);
            return null;
        }
    }

    public string GetTestArtifactsPath()
    {
        var testsProjectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _config.TestsProjectPath));
        if (!Directory.Exists(testsProjectDir) && Directory.Exists(Path.GetFullPath(_config.TestsProjectPath)))
        {
            testsProjectDir = Path.GetFullPath(_config.TestsProjectPath);
        }
        return Path.Combine(testsProjectDir, "TestArtifacts");
    }

    public void OpenTraceViewer(string traceFileName)
    {
        try
        {
            var artifactsDir = GetTestArtifactsPath();
            var tracePath = Path.Combine(artifactsDir, "traces", traceFileName);
            if (!File.Exists(tracePath))
            {
                var binTrace = Path.Combine(Path.GetDirectoryName(artifactsDir)!, "bin", "Debug", "net8.0", "traces", traceFileName);
                if (File.Exists(binTrace))
                {
                    tracePath = binTrace;
                }
            }

            if (File.Exists(tracePath))
            {
                _logger.LogInformation("Spawning Playwright show-trace for {TracePath}", tracePath);
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c npx playwright show-trace \"{tracePath}\"",
                    UseShellExecute = true,
                    CreateNoWindow = true
                };
                Process.Start(psi);
            }
            else
            {
                _logger.LogWarning("Trace file not found: {TracePath}", tracePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch Playwright Trace Viewer for {TraceFile}", traceFileName);
        }
    }
}

