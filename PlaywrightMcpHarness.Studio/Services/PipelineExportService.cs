using System;
using System.Text;

namespace PlaywrightMcpHarness.Studio.Services;

public enum PipelinePlatform
{
    GitHubActions,
    AzureDevOps,
    GitLabCI,
    Docker,
    DockerCompose,
    PowerShellScript,
    BashScript
}

public class PipelineExportOptions
{
    public PipelinePlatform Platform { get; set; } = PipelinePlatform.GitHubActions;
    public string BranchName { get; set; } = "main";
    public string TargetOS { get; set; } = "ubuntu-latest";
    public string DotNetVersion { get; set; } = "8.0.x";
    public string NodeVersion { get; set; } = "20";
    public bool InstallAllBrowsers { get; set; } = false; // false = chromium only (faster)
    public bool RunInParallel { get; set; } = true;
    public bool UploadArtifacts { get; set; } = true;
    public bool CollectTestResults { get; set; } = true;
    public bool EnableCronSchedule { get; set; } = false;
    public string CronExpression { get; set; } = "0 2 * * *"; // Nightly 2 AM UTC
    public double VisualTolerancePercent { get; set; } = 0.2;
}

public class PipelineExportResult
{
    public string SuggestedFileName { get; set; } = "";
    public string SuggestedRelativePath { get; set; } = "";
    public string Content { get; set; } = "";
    public string SyntaxLanguage { get; set; } = "yaml"; // yaml, dockerfile, powershell, shell
    public string Description { get; set; } = "";
}

public class PipelineExportService
{
    public PipelineExportResult GeneratePipeline(PipelineExportOptions options)
    {
        return options.Platform switch
        {
            PipelinePlatform.GitHubActions => GenerateGitHubActions(options),
            PipelinePlatform.AzureDevOps => GenerateAzureDevOps(options),
            PipelinePlatform.GitLabCI => GenerateGitLabCI(options),
            PipelinePlatform.Docker => GenerateDockerfile(options),
            PipelinePlatform.DockerCompose => GenerateDockerCompose(options),
            PipelinePlatform.PowerShellScript => GeneratePowerShellScript(options),
            PipelinePlatform.BashScript => GenerateBashScript(options),
            _ => GenerateGitHubActions(options)
        };
    }

    private PipelineExportResult GenerateGitHubActions(PipelineExportOptions options)
    {
        var sb = new StringBuilder();
        var browserArg = options.InstallAllBrowsers ? "" : "chromium";

        sb.AppendLine("name: Playwright SpecFlow Tests");
        sb.AppendLine();
        sb.AppendLine("on:");
        sb.AppendLine("  push:");
        sb.AppendLine($"    branches: [ {options.BranchName} ]");
        sb.AppendLine("  pull_request:");
        sb.AppendLine($"    branches: [ {options.BranchName} ]");
        if (options.EnableCronSchedule && !string.IsNullOrWhiteSpace(options.CronExpression))
        {
            sb.AppendLine("  schedule:");
            sb.AppendLine($"    - cron: '{options.CronExpression}'");
        }
        sb.AppendLine("  workflow_dispatch:");
        sb.AppendLine();
        sb.AppendLine("jobs:");
        sb.AppendLine("  test:");
        sb.AppendLine($"    runs-on: {options.TargetOS}");
        sb.AppendLine("    timeout-minutes: 30");
        sb.AppendLine();
        sb.AppendLine("    steps:");
        sb.AppendLine("    - name: Checkout Code");
        sb.AppendLine("      uses: actions/checkout@v4");
        sb.AppendLine();
        sb.AppendLine("    - name: Setup .NET SDK");
        sb.AppendLine("      uses: actions/setup-dotnet@v4");
        sb.AppendLine("      with:");
        sb.AppendLine($"        dotnet-version: '{options.DotNetVersion}'");
        sb.AppendLine();
        sb.AppendLine("    - name: Setup Node.js");
        sb.AppendLine("      uses: actions/setup-node@v4");
        sb.AppendLine("      with:");
        sb.AppendLine($"        node-version: {options.NodeVersion}");
        sb.AppendLine();
        sb.AppendLine("    - name: Restore Dependencies");
        sb.AppendLine("      run: dotnet restore PlaywrightMcpHarness.sln");
        sb.AppendLine();
        sb.AppendLine("    - name: Build Test Suite");
        sb.AppendLine("      run: dotnet build PlaywrightMcpHarness.Tests/PlaywrightMcpHarness.Tests.csproj -c Release --no-restore");
        sb.AppendLine();
        sb.AppendLine("    - name: Install Playwright Browsers & OS Dependencies");
        sb.AppendLine("      run: |");
        sb.AppendLine($"        pwsh PlaywrightMcpHarness.Tests/bin/Release/net8.0/playwright.ps1 install --with-deps {browserArg}".TrimEnd());
        sb.AppendLine();
        sb.AppendLine("    - name: Run Playwright SpecFlow Tests");
        sb.AppendLine("      run: |");

        var runsettingsFlag = options.RunInParallel ? "" : " -s PlaywrightMcpHarness.Tests/sequential.runsettings";
        sb.AppendLine($"        dotnet test PlaywrightMcpHarness.Tests/PlaywrightMcpHarness.Tests.csproj \\");
        sb.AppendLine($"          -c Release \\");
        sb.AppendLine($"          --no-build{runsettingsFlag} \\");
        sb.AppendLine($"          --logger \"trx;LogFileName=test-results.trx\" \\");
        sb.AppendLine($"          --logger \"console;verbosity=normal\"");
        sb.AppendLine("      env:");
        sb.AppendLine("        HEADED: 0");
        sb.AppendLine("        SLOWMO: 0");
        sb.AppendLine("        PWDEBUG: 0");
        sb.AppendLine($"        VISUAL_TOLERANCE_PERCENT: {options.VisualTolerancePercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine();

        if (options.CollectTestResults)
        {
            sb.AppendLine("    - name: Upload Test Results (TRX Report)");
            sb.AppendLine("      if: always()");
            sb.AppendLine("      uses: actions/upload-artifact@v4");
            sb.AppendLine("      with:");
            sb.AppendLine("        name: test-results-trx");
            sb.AppendLine("        path: \"**/test-results.trx\"");
            sb.AppendLine("        retention-days: 14");
            sb.AppendLine();
        }

        if (options.UploadArtifacts)
        {
            sb.AppendLine("    - name: Upload Playwright Diagnostics (Traces, Screenshots & Visual Diffs)");
            sb.AppendLine("      if: always()");
            sb.AppendLine("      uses: actions/upload-artifact@v4");
            sb.AppendLine("      with:");
            sb.AppendLine("        name: playwright-test-artifacts");
            sb.AppendLine("        path: PlaywrightMcpHarness.Tests/TestArtifacts/");
            sb.AppendLine("        retention-days: 14");
        }

        return new PipelineExportResult
        {
            SuggestedFileName = "playwright-tests.yml",
            SuggestedRelativePath = ".github/workflows/playwright-tests.yml",
            Content = sb.ToString(),
            SyntaxLanguage = "yaml",
            Description = "Automated GitHub Actions CI workflow with Playwright browser provisioning, TRX test reporting, and diagnostic artifact archival."
        };
    }

    private PipelineExportResult GenerateAzureDevOps(PipelineExportOptions options)
    {
        var sb = new StringBuilder();
        var browserArg = options.InstallAllBrowsers ? "" : "chromium";

        sb.AppendLine("trigger:");
        sb.AppendLine("  branches:");
        sb.AppendLine("    include:");
        sb.AppendLine($"      - {options.BranchName}");
        sb.AppendLine();
        sb.AppendLine("pr:");
        sb.AppendLine("  branches:");
        sb.AppendLine("    include:");
        sb.AppendLine($"      - {options.BranchName}");
        sb.AppendLine();
        if (options.EnableCronSchedule && !string.IsNullOrWhiteSpace(options.CronExpression))
        {
            sb.AppendLine("schedules:");
            sb.AppendLine($"  - cron: '{options.CronExpression}'");
            sb.AppendLine("    displayName: 'Nightly Automated Playwright Test Suite'");
            sb.AppendLine("    branches:");
            sb.AppendLine("      include:");
            sb.AppendLine($"        - {options.BranchName}");
            sb.AppendLine("    always: true");
            sb.AppendLine();
        }
        sb.AppendLine("pool:");
        sb.AppendLine($"  vmImage: '{options.TargetOS}'");
        sb.AppendLine();
        sb.AppendLine("variables:");
        sb.AppendLine("  buildConfiguration: 'Release'");
        sb.AppendLine("  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: 'true'");
        sb.AppendLine();
        sb.AppendLine("steps:");
        sb.AppendLine("- task: UseDotNet@2");
        sb.AppendLine("  displayName: 'Install .NET SDK'");
        sb.AppendLine("  inputs:");
        sb.AppendLine("    packageType: 'sdk'");
        sb.AppendLine($"    version: '{options.DotNetVersion}'");
        sb.AppendLine();
        sb.AppendLine("- task: NodeTool@0");
        sb.AppendLine("  displayName: 'Install Node.js'");
        sb.AppendLine("  inputs:");
        sb.AppendLine($"    versionSpec: '{options.NodeVersion}.x'");
        sb.AppendLine();
        sb.AppendLine("- script: dotnet restore PlaywrightMcpHarness.sln");
        sb.AppendLine("  displayName: 'Restore NuGet Packages'");
        sb.AppendLine();
        sb.AppendLine("- script: dotnet build PlaywrightMcpHarness.Tests/PlaywrightMcpHarness.Tests.csproj --configuration $(buildConfiguration) --no-restore");
        sb.AppendLine("  displayName: 'Build Test Suite'");
        sb.AppendLine();
        sb.AppendLine($"- script: pwsh PlaywrightMcpHarness.Tests/bin/$(buildConfiguration)/net8.0/playwright.ps1 install --with-deps {browserArg}".TrimEnd());
        sb.AppendLine("  displayName: 'Install Playwright Browsers & OS Dependencies'");
        sb.AppendLine();

        var runsettingsFlag = options.RunInParallel ? "" : " -s PlaywrightMcpHarness.Tests/sequential.runsettings";
        sb.AppendLine("- script: |");
        sb.AppendLine($"    dotnet test PlaywrightMcpHarness.Tests/PlaywrightMcpHarness.Tests.csproj \\");
        sb.AppendLine($"      --configuration $(buildConfiguration) \\");
        sb.AppendLine($"      --no-build{runsettingsFlag} \\");
        sb.AppendLine($"      --logger \"trx;LogFileName=test-results.trx\" \\");
        sb.AppendLine($"      --logger \"console;verbosity=normal\"");
        sb.AppendLine("  displayName: 'Execute Playwright SpecFlow Tests'");
        sb.AppendLine("  env:");
        sb.AppendLine("    HEADED: 0");
        sb.AppendLine("    SLOWMO: 0");
        sb.AppendLine("    PWDEBUG: 0");
        sb.AppendLine($"    VISUAL_TOLERANCE_PERCENT: {options.VisualTolerancePercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine();

        if (options.CollectTestResults)
        {
            sb.AppendLine("- task: PublishTestResults@2");
            sb.AppendLine("  displayName: 'Publish VSTest / TRX Test Results'");
            sb.AppendLine("  condition: always()");
            sb.AppendLine("  inputs:");
            sb.AppendLine("    testResultsFormat: 'VSTest'");
            sb.AppendLine("    testResultsFiles: '**/test-results.trx'");
            sb.AppendLine("    mergeTestResults: true");
            sb.AppendLine("    failTaskOnFailedTests: true");
            sb.AppendLine();
        }

        if (options.UploadArtifacts)
        {
            sb.AppendLine("- task: PublishPipelineArtifact@1");
            sb.AppendLine("  displayName: 'Archive Playwright Diagnostic Artifacts (Traces & Visual Diffs)'");
            sb.AppendLine("  condition: always()");
            sb.AppendLine("  inputs:");
            sb.AppendLine("    targetPath: 'PlaywrightMcpHarness.Tests/TestArtifacts'");
            sb.AppendLine("    artifactName: 'PlaywrightDiagnostics'");
        }

        return new PipelineExportResult
        {
            SuggestedFileName = "azure-pipelines.yml",
            SuggestedRelativePath = "azure-pipelines.yml",
            Content = sb.ToString(),
            SyntaxLanguage = "yaml",
            Description = "Enterprise Azure DevOps pipeline with VSTest test tab integration and pipeline artifact publishing."
        };
    }

    private PipelineExportResult GenerateGitLabCI(PipelineExportOptions options)
    {
        var sb = new StringBuilder();
        var browserArg = options.InstallAllBrowsers ? "" : "chromium";

        sb.AppendLine("stages:");
        sb.AppendLine("  - test");
        sb.AppendLine();
        sb.AppendLine("playwright_specflow_tests:");
        sb.AppendLine("  stage: test");
        sb.AppendLine("  image: mcr.microsoft.com/playwright/dotnet:v1.50.0-jammy");
        sb.AppendLine("  variables:");
        sb.AppendLine("    HEADED: \"0\"");
        sb.AppendLine("    SLOWMO: \"0\"");
        sb.AppendLine("    PWDEBUG: \"0\"");
        sb.AppendLine($"    VISUAL_TOLERANCE_PERCENT: \"{options.VisualTolerancePercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"");
        sb.AppendLine("  before_script:");
        sb.AppendLine("    - dotnet restore PlaywrightMcpHarness.sln");
        sb.AppendLine("    - dotnet build PlaywrightMcpHarness.Tests/PlaywrightMcpHarness.Tests.csproj -c Release --no-restore");
        sb.AppendLine($"    - pwsh PlaywrightMcpHarness.Tests/bin/Release/net8.0/playwright.ps1 install {browserArg}".TrimEnd());
        sb.AppendLine("  script:");

        var runsettingsFlag = options.RunInParallel ? "" : " -s PlaywrightMcpHarness.Tests/sequential.runsettings";
        sb.AppendLine($"    - dotnet test PlaywrightMcpHarness.Tests/PlaywrightMcpHarness.Tests.csproj -c Release --no-build{runsettingsFlag} --logger \"junit;LogFileName=junit-test-results.xml\" --logger \"console;verbosity=normal\"");

        sb.AppendLine("  artifacts:");
        sb.AppendLine("    when: always");
        if (options.CollectTestResults)
        {
            sb.AppendLine("    reports:");
            sb.AppendLine("      junit: \"**/junit-test-results.xml\"");
        }
        if (options.UploadArtifacts)
        {
            sb.AppendLine("    paths:");
            sb.AppendLine("      - PlaywrightMcpHarness.Tests/TestArtifacts/");
        }
        sb.AppendLine("    expire_in: 14 days");
        sb.AppendLine("  rules:");
        sb.AppendLine($"    - if: $CI_COMMIT_BRANCH == \"{options.BranchName}\"");
        sb.AppendLine("    - if: $CI_PIPELINE_SOURCE == \"merge_request_event\"");
        if (options.EnableCronSchedule)
        {
            sb.AppendLine("    - if: $CI_PIPELINE_SOURCE == \"schedule\"");
        }

        return new PipelineExportResult
        {
            SuggestedFileName = ".gitlab-ci.yml",
            SuggestedRelativePath = ".gitlab-ci.yml",
            Content = sb.ToString(),
            SyntaxLanguage = "yaml",
            Description = "GitLab CI pipeline leveraging Microsoft's official Playwright container with JUnit test widget integration."
        };
    }

    private PipelineExportResult GenerateDockerfile(PipelineExportOptions options)
    {
        var sb = new StringBuilder();
        var browserArg = options.InstallAllBrowsers ? "" : "chromium";

        sb.AppendLine("# Official Microsoft Playwright .NET base image with pre-installed Linux browser dependencies");
        sb.AppendLine("FROM mcr.microsoft.com/playwright/dotnet:v1.50.0-jammy AS testrunner");
        sb.AppendLine();
        sb.AppendLine("WORKDIR /app");
        sb.AppendLine();
        sb.AppendLine("# Copy solution and restore dependencies first to leverage Docker layer caching");
        sb.AppendLine("COPY PlaywrightMcpHarness.sln .");
        sb.AppendLine("COPY PlaywrightMcpHarness.Studio/PlaywrightMcpHarness.Studio.csproj PlaywrightMcpHarness.Studio/");
        sb.AppendLine("COPY PlaywrightMcpHarness.Tests/PlaywrightMcpHarness.Tests.csproj PlaywrightMcpHarness.Tests/");
        sb.AppendLine("RUN dotnet restore PlaywrightMcpHarness.sln");
        sb.AppendLine();
        sb.AppendLine("# Copy remaining repository source files");
        sb.AppendLine("COPY . .");
        sb.AppendLine();
        sb.AppendLine("# Build tests and install headless browsers");
        sb.AppendLine("RUN dotnet build PlaywrightMcpHarness.Tests/PlaywrightMcpHarness.Tests.csproj -c Release --no-restore");
        sb.AppendLine($"RUN pwsh PlaywrightMcpHarness.Tests/bin/Release/net8.0/playwright.ps1 install {browserArg}".TrimEnd());
        sb.AppendLine();
        sb.AppendLine("# Configure execution environment");
        sb.AppendLine("ENV HEADED=0 \\");
        sb.AppendLine("    SLOWMO=0 \\");
        sb.AppendLine("    PWDEBUG=0 \\");
        sb.AppendLine($"    VISUAL_TOLERANCE_PERCENT={options.VisualTolerancePercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine();

        var runsettingsFlag = options.RunInParallel ? "" : " -s PlaywrightMcpHarness.Tests/sequential.runsettings";
        sb.AppendLine($"ENTRYPOINT [\"dotnet\", \"test\", \"PlaywrightMcpHarness.Tests/PlaywrightMcpHarness.Tests.csproj\", \"-c\", \"Release\", \"--no-build\"{runsettingsFlag.Replace(" ", "\", \"")}, \"--logger\", \"console;verbosity=normal\"]");

        return new PipelineExportResult
        {
            SuggestedFileName = "Dockerfile.test",
            SuggestedRelativePath = "Dockerfile.test",
            Content = sb.ToString(),
            SyntaxLanguage = "dockerfile",
            Description = "Hermetic Docker container for running Playwright test suites in isolated CI agents or local Docker environments."
        };
    }

    private PipelineExportResult GenerateDockerCompose(PipelineExportOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("version: '3.8'");
        sb.AppendLine();
        sb.AppendLine("services:");
        sb.AppendLine("  playwright-tests:");
        sb.AppendLine("    build:");
        sb.AppendLine("      context: .");
        sb.AppendLine("      dockerfile: Dockerfile.test");
        sb.AppendLine("    volumes:");
        sb.AppendLine("      - ./PlaywrightMcpHarness.Tests/TestArtifacts:/app/PlaywrightMcpHarness.Tests/TestArtifacts");
        sb.AppendLine("      - ./PlaywrightMcpHarness.Tests/VisualBaselines:/app/PlaywrightMcpHarness.Tests/VisualBaselines");
        sb.AppendLine("    environment:");
        sb.AppendLine("      - HEADED=0");
        sb.AppendLine("      - SLOWMO=0");
        sb.AppendLine($"      - VISUAL_TOLERANCE_PERCENT={options.VisualTolerancePercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine("    ipc: host");

        return new PipelineExportResult
        {
            SuggestedFileName = "docker-compose.test.yml",
            SuggestedRelativePath = "docker-compose.test.yml",
            Content = sb.ToString(),
            SyntaxLanguage = "yaml",
            Description = "Docker Compose specification mapping volume mounts for Playwright traces, screenshots, and visual golden baselines."
        };
    }

    private PipelineExportResult GeneratePowerShellScript(PipelineExportOptions options)
    {
        var sb = new StringBuilder();
        var browserArg = options.InstallAllBrowsers ? "" : "chromium";

        sb.AppendLine("<#");
        sb.AppendLine(".SYNOPSIS");
        sb.AppendLine("    Cross-platform automated Playwright SpecFlow test runner script.");
        sb.AppendLine(".EXAMPLE");
        sb.AppendLine("    ./run-tests.ps1");
        sb.AppendLine("    ./run-tests.ps1 -Headed -SlowMo 500");
        sb.AppendLine("    ./run-tests.ps1 -Filter \"FullyQualifiedName~VisualRegression\"");
        sb.AppendLine("#>");
        sb.AppendLine("param(");
        sb.AppendLine("    [switch]$Headed,");
        sb.AppendLine("    [int]$SlowMo = 0,");
        sb.AppendLine("    [switch]$Sequential,");
        sb.AppendLine("    [string]$Filter = \"\",");
        sb.AppendLine("    [string]$Configuration = \"Debug\"");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine();
        sb.AppendLine("Write-Host \"[Harness] Checking prerequisites...\" -ForegroundColor Cyan");
        sb.AppendLine("if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {");
        sb.AppendLine("    Write-Error \".NET 8 SDK is required but not found in PATH.\"");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Write-Host \"[Harness] Building test suite ($Configuration)...\" -ForegroundColor Cyan");
        sb.AppendLine("dotnet build PlaywrightMcpHarness.Tests/PlaywrightMcpHarness.Tests.csproj -c $Configuration");
        sb.AppendLine();
        sb.AppendLine("$playwrightScript = \"PlaywrightMcpHarness.Tests/bin/$Configuration/net8.0/playwright.ps1\"");
        sb.AppendLine("if (Test-Path $playwrightScript) {");
        sb.AppendLine($"    Write-Host \"[Harness] Ensuring Playwright browser binaries are present...\" -ForegroundColor Cyan");
        sb.AppendLine($"    pwsh $playwrightScript install {browserArg}".TrimEnd());
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("$env:HEADED = if ($Headed) { '1' } else { '0' }");
        sb.AppendLine("$env:SLOWMO = $SlowMo.ToString()");
        sb.AppendLine("$env:PWDEBUG = '0'");
        sb.AppendLine($"$env:VISUAL_TOLERANCE_PERCENT = '{options.VisualTolerancePercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}'");
        sb.AppendLine();
        sb.AppendLine("$extraArgs = @()");
        sb.AppendLine("if ($Sequential) {");
        sb.AppendLine("    $extraArgs += '-s', 'PlaywrightMcpHarness.Tests/sequential.runsettings'");
        sb.AppendLine("}");
        sb.AppendLine("if ($Filter) {");
        sb.AppendLine("    $extraArgs += '--filter', $Filter");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Write-Host \"[Harness] Launching dotnet test...\" -ForegroundColor Green");
        sb.AppendLine("& dotnet test PlaywrightMcpHarness.Tests/PlaywrightMcpHarness.Tests.csproj -c $Configuration --no-build @extraArgs --logger \"console;verbosity=normal\"");
        sb.AppendLine("exit $LASTEXITCODE");

        return new PipelineExportResult
        {
            SuggestedFileName = "run-tests.ps1",
            SuggestedRelativePath = "run-tests.ps1",
            Content = sb.ToString(),
            SyntaxLanguage = "powershell",
            Description = "Native PowerShell script for flexible local or CI execution with parameter flags for Headed, SlowMo, Concurrency, and Test Filters."
        };
    }

    private PipelineExportResult GenerateBashScript(PipelineExportOptions options)
    {
        var sb = new StringBuilder();
        var browserArg = options.InstallAllBrowsers ? "" : "chromium";

        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("# =============================================================================");
        sb.AppendLine("# Cross-platform POSIX Bash test runner for Playwright + SpecFlow");
        sb.AppendLine("# Usage: ./run-tests.sh [--headed] [--slowmo 500] [--sequential] [--filter <expr>]");
        sb.AppendLine("# =============================================================================");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine();
        sb.AppendLine("HEADED=0");
        sb.AppendLine("SLOWMO=0");
        sb.AppendLine("SEQUENTIAL=0");
        sb.AppendLine("FILTER=\"\"");
        sb.AppendLine("CONFIG=\"Debug\"");
        sb.AppendLine();
        sb.AppendLine("while [[ $# -gt 0 ]]; do");
        sb.AppendLine("  case \"$1\" in");
        sb.AppendLine("    --headed) HEADED=1; shift ;;");
        sb.AppendLine("    --slowmo) SLOWMO=\"$2\"; shift 2 ;;");
        sb.AppendLine("    --sequential) SEQUENTIAL=1; shift ;;");
        sb.AppendLine("    --filter) FILTER=\"$2\"; shift 2 ;;");
        sb.AppendLine("    --release) CONFIG=\"Release\"; shift ;;");
        sb.AppendLine("    *) echo \"Unknown option: $1\"; exit 1 ;;");
        sb.AppendLine("  esac");
        sb.AppendLine("done");
        sb.AppendLine();
        sb.AppendLine("echo \"==> Building Playwright test suite ($CONFIG)...\"");
        sb.AppendLine("dotnet build PlaywrightMcpHarness.Tests/PlaywrightMcpHarness.Tests.csproj -c \"$CONFIG\"");
        sb.AppendLine();
        sb.AppendLine("PW_SCRIPT=\"PlaywrightMcpHarness.Tests/bin/$CONFIG/net8.0/playwright.ps1\"");
        sb.AppendLine("if [ -f \"$PW_SCRIPT\" ]; then");
        sb.AppendLine("  echo \"==> Installing Playwright browsers...\"");
        sb.AppendLine($"  pwsh \"$PW_SCRIPT\" install {browserArg}".TrimEnd());
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("export HEADED=\"$HEADED\"");
        sb.AppendLine("export SLOWMO=\"$SLOWMO\"");
        sb.AppendLine("export PWDEBUG=0");
        sb.AppendLine($"export VISUAL_TOLERANCE_PERCENT=\"{options.VisualTolerancePercent.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"");
        sb.AppendLine();
        sb.AppendLine("EXTRA_ARGS=()");
        sb.AppendLine("if [ \"$SEQUENTIAL\" -eq 1 ]; then");
        sb.AppendLine("  EXTRA_ARGS+=(\"-s\" \"PlaywrightMcpHarness.Tests/sequential.runsettings\")");
        sb.AppendLine("fi");
        sb.AppendLine("if [ -n \"$FILTER\" ]; then");
        sb.AppendLine("  EXTRA_ARGS+=(\"--filter\" \"$FILTER\")");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("echo \"==> Executing test runner...\"");
        sb.AppendLine("dotnet test PlaywrightMcpHarness.Tests/PlaywrightMcpHarness.Tests.csproj \\");
        sb.AppendLine("  -c \"$CONFIG\" \\");
        sb.AppendLine("  --no-build \\");
        sb.AppendLine("  \"${EXTRA_ARGS[@]}\" \\");
        sb.AppendLine("  --logger \"console;verbosity=normal\"");

        return new PipelineExportResult
        {
            SuggestedFileName = "run-tests.sh",
            SuggestedRelativePath = "run-tests.sh",
            Content = sb.ToString(),
            SyntaxLanguage = "shell",
            Description = "POSIX-compliant Bash test runner script for Linux/macOS developer workstations and CI runners."
        };
    }
}
