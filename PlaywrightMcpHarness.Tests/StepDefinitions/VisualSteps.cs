using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;
using PlaywrightMcpHarness.Tests.Common;
using TechTalk.SpecFlow;
using Xunit.Sdk;

namespace PlaywrightMcpHarness.Tests.StepDefinitions;

[Binding]
public class VisualSteps
{
    private readonly IPage _page;

    public VisualSteps(IPage page)
    {
        _page = page;
    }

    [Then(@"the page appearance should match baseline ""(.*)""")]
    public async Task ThenThePageAppearanceShouldMatchBaselineAsync(string snapshotName)
    {
        await AssertPageAppearanceMatchesBaselineAsync(snapshotName, GetDefaultTolerance());
    }

    [Then(@"the page appearance should match baseline ""(.*)"" with tolerance ([0-9.]+)%")]
    public async Task ThenThePageAppearanceShouldMatchBaselineWithToleranceAsync(string snapshotName, double tolerancePercent)
    {
        await AssertPageAppearanceMatchesBaselineAsync(snapshotName, tolerancePercent);
    }

    [Then(@"the ""(.*)"" element appearance should match baseline ""(.*)""")]
    public async Task ThenTheElementAppearanceShouldMatchBaselineAsync(string elementSelector, string snapshotName)
    {
        await AssertElementAppearanceMatchesBaselineAsync(elementSelector, snapshotName, GetDefaultTolerance());
    }

    [Then(@"the ""(.*)"" element appearance should match baseline ""(.*)"" with tolerance ([0-9.]+)%")]
    public async Task ThenTheElementAppearanceShouldMatchBaselineWithToleranceAsync(string elementSelector, string snapshotName, double tolerancePercent)
    {
        await AssertElementAppearanceMatchesBaselineAsync(elementSelector, snapshotName, tolerancePercent);
    }

    private async Task AssertPageAppearanceMatchesBaselineAsync(string snapshotName, double tolerancePercent)
    {
        var sanitizedName = SanitizeSnapshotName(snapshotName);
        var (baselinePath, actualPath, diffPath) = ResolveVisualPaths(sanitizedName);

        // Take high-fidelity screenshot with animations disabled for consistency
        await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await Task.Delay(250); // Settle rendering

        await _page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = actualPath,
            FullPage = true,
            Animations = ScreenshotAnimations.Disabled
        });

        PerformVisualComparison(sanitizedName, baselinePath, actualPath, diffPath, tolerancePercent);
    }

    private async Task AssertElementAppearanceMatchesBaselineAsync(string selector, string snapshotName, double tolerancePercent)
    {
        var sanitizedName = SanitizeSnapshotName(snapshotName);
        var (baselinePath, actualPath, diffPath) = ResolveVisualPaths(sanitizedName);

        ILocator locator;
        if (selector.StartsWith("text=", StringComparison.OrdinalIgnoreCase))
        {
            locator = _page.GetByText(selector.Substring(5));
        }
        else if (selector.StartsWith("role=", StringComparison.OrdinalIgnoreCase))
        {
            var roleName = selector.Substring(5);
            locator = _page.GetByRole(AriaRole.Region, new() { Name = roleName });
        }
        else
        {
            locator = _page.Locator(selector);
        }

        await locator.WaitForAsync();
        await locator.ScreenshotAsync(new LocatorScreenshotOptions
        {
            Path = actualPath,
            Animations = ScreenshotAnimations.Disabled
        });

        PerformVisualComparison(sanitizedName, baselinePath, actualPath, diffPath, tolerancePercent);
    }

    private static void PerformVisualComparison(
        string snapshotName,
        string baselinePath,
        string actualPath,
        string diffPath,
        double tolerancePercent)
    {
        if (!File.Exists(baselinePath))
        {
            // Initial run: Save actual screenshot as the new golden baseline!
            var baselineDir = Path.GetDirectoryName(baselinePath)!;
            if (!Directory.Exists(baselineDir))
            {
                Directory.CreateDirectory(baselineDir);
            }

            File.Copy(actualPath, baselinePath, overwrite: true);
            Console.WriteLine($"[VISUAL_BASELINE_CREATED] {snapshotName}.png");
            return;
        }

        // Compare against existing golden baseline
        var baselineImage = SimplePngImage.FromFile(baselinePath);
        var actualImage = SimplePngImage.FromFile(actualPath);

        var result = VisualComparer.Compare(baselineImage, actualImage, tolerancePercent);
        result.SnapshotName = snapshotName;

        if (result.Passed)
        {
            // Cleanup any previous diff artifact on successful pass
            if (File.Exists(diffPath))
            {
                File.Delete(diffPath);
            }

            Console.WriteLine($"[VISUAL_DIFF_PASSED] {snapshotName} Mismatch: {result.MismatchPercentage:F2}% (Tolerance: {tolerancePercent:F2}%)");
        }
        else
        {
            // Save highlighted diff image
            result.DiffImage?.SaveToFile(diffPath);

            Console.WriteLine($"[VISUAL_DIFF_FAILED] {snapshotName} Mismatch: {result.MismatchPercentage:F2}% (Tolerance: {tolerancePercent:F2}%) Actual: {snapshotName}.actual.png Diff: {snapshotName}.diff.png");
            throw new XunitException($"Visual regression detected for '{snapshotName}'. {result.Message}");
        }
    }

    private static (string BaselinePath, string ActualPath, string DiffPath) ResolveVisualPaths(string snapshotName)
    {
        var testsRoot = AppContext.BaseDirectory;
        // Locate source project root if running under bin/Debug/...
        var current = new DirectoryInfo(testsRoot);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "PlaywrightMcpHarness.Tests.csproj")))
        {
            current = current.Parent;
        }

        var projectRoot = current?.FullName ?? Directory.GetCurrentDirectory();
        var baselinePath = Path.Combine(projectRoot, "VisualBaselines", $"{snapshotName}.png");

        var artifactsDir = Environment.GetEnvironmentVariable("TEST_ARTIFACTS_DIR");
        if (string.IsNullOrWhiteSpace(artifactsDir))
        {
            artifactsDir = Path.Combine(projectRoot, "TestArtifacts");
        }

        var visualDir = Path.Combine(artifactsDir, "visual");
        if (!Directory.Exists(visualDir))
        {
            Directory.CreateDirectory(visualDir);
        }

        var actualPath = Path.Combine(visualDir, $"{snapshotName}.actual.png");
        var diffPath = Path.Combine(visualDir, $"{snapshotName}.diff.png");

        return (baselinePath, actualPath, diffPath);
    }

    private static double GetDefaultTolerance()
    {
        var env = Environment.GetEnvironmentVariable("VISUAL_TOLERANCE_PERCENT");
        return double.TryParse(env, out var val) ? val : 0.2;
    }

    private static string SanitizeSnapshotName(string raw)
    {
        var clean = System.Text.RegularExpressions.Regex.Replace(raw, @"[^a-zA-Z0-9_\-]", "_");
        return clean.Trim('_');
    }
}
