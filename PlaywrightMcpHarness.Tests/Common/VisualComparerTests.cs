using System;
using System.IO;
using Xunit;

namespace PlaywrightMcpHarness.Tests.Common;

public class VisualComparerTests
{
    [Fact]
    public void BlankImage_HasExpectedDimensionsAndColor()
    {
        var img = SimplePngImage.CreateBlank(16, 16, 255, 0, 100, 255);
        Assert.Equal(16, img.Width);
        Assert.Equal(16, img.Height);

        var (r, g, b, a) = img.GetPixel(0, 0);
        Assert.Equal(255, r);
        Assert.Equal(0, g);
        Assert.Equal(100, b);
        Assert.Equal(255, a);
    }

    [Fact]
    public void EncodeAndDecode_RoundTripsPixelData()
    {
        var original = SimplePngImage.CreateBlank(8, 8, 30, 60, 90, 255);
        original.SetPixel(2, 3, 200, 150, 100, 255);

        using var ms = new MemoryStream();
        original.Save(ms);
        ms.Position = 0;

        var loaded = SimplePngImage.FromStream(ms);
        Assert.Equal(original.Width, loaded.Width);
        Assert.Equal(original.Height, loaded.Height);

        var pixel = loaded.GetPixel(2, 3);
        Assert.Equal(200, pixel.R);
        Assert.Equal(150, pixel.G);
        Assert.Equal(100, pixel.B);
        Assert.Equal(255, pixel.A);

        var bgPixel = loaded.GetPixel(0, 0);
        Assert.Equal(30, bgPixel.R);
        Assert.Equal(60, bgPixel.G);
        Assert.Equal(90, bgPixel.B);
    }

    [Fact]
    public void IdenticalImages_PassWithZeroMismatch()
    {
        var imgA = SimplePngImage.CreateBlank(20, 20, 120, 120, 120, 255);
        var imgB = SimplePngImage.CreateBlank(20, 20, 120, 120, 120, 255);

        var result = VisualComparer.Compare(imgA, imgB, tolerancePercentage: 0.1);

        Assert.True(result.Passed);
        Assert.Equal(0, result.MismatchedPixels);
        Assert.Equal(0.0, result.MismatchPercentage);
    }

    [Fact]
    public void DifferentImages_DetectsPixelMismatchAndProducesDiffImage()
    {
        // 100 total pixels (10x10)
        var baseline = SimplePngImage.CreateBlank(10, 10, 255, 255, 255, 255);
        var actual = SimplePngImage.CreateBlank(10, 10, 255, 255, 255, 255);

        // Change 5 pixels (5%)
        actual.SetPixel(0, 0, 0, 0, 0, 255);
        actual.SetPixel(0, 1, 0, 0, 0, 255);
        actual.SetPixel(0, 2, 0, 0, 0, 255);
        actual.SetPixel(0, 3, 0, 0, 0, 255);
        actual.SetPixel(0, 4, 0, 0, 0, 255);

        // Tolerance of 1% should FAIL
        var failResult = VisualComparer.Compare(baseline, actual, tolerancePercentage: 1.0);
        Assert.False(failResult.Passed);
        Assert.Equal(5, failResult.MismatchedPixels);
        Assert.Equal(5.0, failResult.MismatchPercentage);
        Assert.NotNull(failResult.DiffImage);

        // Verify diff image has magenta pixel at mismatched position
        var diffPixel = failResult.DiffImage!.GetPixel(0, 0);
        Assert.Equal(255, diffPixel.R);
        Assert.Equal(0, diffPixel.G);
        Assert.Equal(100, diffPixel.B); // Magenta #FF0064

        // Tolerance of 6% should PASS
        var passResult = VisualComparer.Compare(baseline, actual, tolerancePercentage: 6.0);
        Assert.True(passResult.Passed);
    }

    [Fact]
    public void DimensionMismatch_ReportsError()
    {
        var imgA = SimplePngImage.CreateBlank(10, 10, 255, 255, 255, 255);
        var imgB = SimplePngImage.CreateBlank(15, 10, 255, 255, 255, 255);

        var result = VisualComparer.Compare(imgA, imgB, tolerancePercentage: 0.2);
        Assert.False(result.Passed);
        Assert.Contains("dimensions differ", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateDemoSnapshots_CreatesRealisticGoldenAndDiffSamples()
    {
        var testsRoot = AppContext.BaseDirectory;
        var current = new DirectoryInfo(testsRoot);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "PlaywrightMcpHarness.Tests.csproj")))
        {
            current = current.Parent;
        }
        var projectRoot = current?.FullName ?? Directory.GetCurrentDirectory();

        var baselinesDir = Path.Combine(projectRoot, "VisualBaselines");
        var visualDir = Path.Combine(projectRoot, "TestArtifacts", "visual");
        Directory.CreateDirectory(baselinesDir);
        Directory.CreateDirectory(visualDir);

        // 1. Landing Page Snapshot (400x250)
        var baseline = SimplePngImage.CreateBlank(400, 250, 15, 23, 42, 255); // #0f172a
        // Header
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 400; x++)
                baseline.SetPixel(x, y, 30, 41, 59, 255); // #1e293b
        // CTA Button in baseline: Cyan #38bdf8
        for (int y = 140; y < 175; y++)
            for (int x = 120; x < 280; x++)
                baseline.SetPixel(x, y, 56, 189, 248, 255);

        var actual = SimplePngImage.CreateBlank(400, 250, 15, 23, 42, 255);
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 400; x++)
                actual.SetPixel(x, y, 30, 41, 59, 255);
        // Button in actual shifted to purple #818cf8 and slightly wider
        for (int y = 140; y < 175; y++)
            for (int x = 110; x < 290; x++)
                actual.SetPixel(x, y, 129, 140, 248, 255);

        var baselinePath = Path.Combine(baselinesDir, "landing_page_hero.png");
        var actualPath = Path.Combine(visualDir, "landing_page_hero.actual.png");
        var diffPath = Path.Combine(visualDir, "landing_page_hero.diff.png");

        baseline.SaveToFile(baselinePath);
        actual.SaveToFile(actualPath);

        var comp = VisualComparer.Compare(baseline, actual, tolerancePercentage: 0.2);
        comp.DiffImage?.SaveToFile(diffPath);

        // 2. Clean Baseline without diff: theme_toggle_button (120x60)
        var cleanBaseline = SimplePngImage.CreateBlank(120, 60, 24, 24, 27, 255);
        for (int y = 15; y < 45; y++)
            for (int x = 20; x < 100; x++)
                cleanBaseline.SetPixel(x, y, 99, 102, 241, 255); // Indigo button

        var cleanBaselinePath = Path.Combine(baselinesDir, "theme_toggle_button.png");
        cleanBaseline.SaveToFile(cleanBaselinePath);

        Assert.True(File.Exists(baselinePath));
        Assert.True(File.Exists(actualPath));
        Assert.True(File.Exists(diffPath));
        Assert.True(File.Exists(cleanBaselinePath));
    }
}
