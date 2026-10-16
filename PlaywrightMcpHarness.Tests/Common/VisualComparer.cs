using System;

namespace PlaywrightMcpHarness.Tests.Common;

public class VisualComparisonResult
{
    public string SnapshotName { get; set; } = "";
    public bool Passed { get; set; }
    public long TotalPixels { get; set; }
    public long MismatchedPixels { get; set; }
    public double MismatchPercentage { get; set; }
    public double TolerancePercentage { get; set; }
    public bool DimensionMismatch { get; set; }
    public (int Width, int Height) BaselineSize { get; set; }
    public (int Width, int Height) ActualSize { get; set; }
    public SimplePngImage? DiffImage { get; set; }
    public string Message { get; set; } = "";
}

public static class VisualComparer
{
    /// <summary>
    /// Compares two images and returns a detailed pixel diff analysis with a highlighted diff image.
    /// </summary>
    /// <param name="baseline">Expected golden reference image.</param>
    /// <param name="actual">Actual browser screenshot.</param>
    /// <param name="tolerancePercentage">Max allowed mismatch percentage (e.g. 0.2%).</param>
    /// <param name="colorDistanceThreshold">RGB Euclidean/Manhattan distance threshold to tolerate slight subpixel rendering.</param>
    public static VisualComparisonResult Compare(
        SimplePngImage baseline,
        SimplePngImage actual,
        double tolerancePercentage = 0.2,
        int colorDistanceThreshold = 18)
    {
        int maxWidth = Math.Max(baseline.Width, actual.Width);
        int maxHeight = Math.Max(baseline.Height, actual.Height);
        bool dimensionMismatch = baseline.Width != actual.Width || baseline.Height != actual.Height;

        long totalPixels = (long)maxWidth * maxHeight;
        long mismatchedPixels = 0;

        var diffImage = new SimplePngImage(maxWidth, maxHeight);

        for (int y = 0; y < maxHeight; y++)
        {
            int rowOffset = y * maxWidth * 4;

            for (int x = 0; x < maxWidth; x++)
            {
                int destPixel = rowOffset + x * 4;

                bool inBaseline = x < baseline.Width && y < baseline.Height;
                bool inActual = x < actual.Width && y < actual.Height;

                if (!inBaseline || !inActual)
                {
                    // Dimension mismatch pixel: highlight in bright yellow/amber
                    mismatchedPixels++;
                    diffImage.RgbaPixels[destPixel] = 255;     // R
                    diffImage.RgbaPixels[destPixel + 1] = 190; // G
                    diffImage.RgbaPixels[destPixel + 2] = 0;   // B
                    diffImage.RgbaPixels[destPixel + 3] = 255; // A
                    continue;
                }

                int basePixel = (y * baseline.Width + x) * 4;
                int actPixel = (y * actual.Width + x) * 4;

                byte br = baseline.RgbaPixels[basePixel];
                byte bg = baseline.RgbaPixels[basePixel + 1];
                byte bb = baseline.RgbaPixels[basePixel + 2];
                byte ba = baseline.RgbaPixels[basePixel + 3];

                byte ar = actual.RgbaPixels[actPixel];
                byte ag = actual.RgbaPixels[actPixel + 1];
                byte ab = actual.RgbaPixels[actPixel + 2];
                byte aa = actual.RgbaPixels[actPixel + 3];

                int colorDist = Math.Abs(br - ar) + Math.Abs(bg - ag) + Math.Abs(bb - ab) + Math.Abs(ba - aa);

                if (colorDist > colorDistanceThreshold)
                {
                    // Pixel differs: paint bright neon magenta/red
                    mismatchedPixels++;
                    diffImage.RgbaPixels[destPixel] = 255;     // R
                    diffImage.RgbaPixels[destPixel + 1] = 0;   // G
                    diffImage.RgbaPixels[destPixel + 2] = 100; // B
                    diffImage.RgbaPixels[destPixel + 3] = 255; // A
                }
                else
                {
                    // Pixel matches: draw faint dimmed grayscale of baseline for clear spatial context
                    byte gray = (byte)((br * 0.299 + bg * 0.587 + bb * 0.114) * 0.35 + 40);
                    diffImage.RgbaPixels[destPixel] = gray;
                    diffImage.RgbaPixels[destPixel + 1] = gray;
                    diffImage.RgbaPixels[destPixel + 2] = gray;
                    diffImage.RgbaPixels[destPixel + 3] = 255;
                }
            }
        }

        double mismatchPercentage = totalPixels > 0 ? (double)mismatchedPixels / totalPixels * 100.0 : 0.0;
        bool passed = !dimensionMismatch && mismatchPercentage <= tolerancePercentage;

        string message = passed
            ? $"Visual check passed: {mismatchPercentage:F2}% diff (tolerance: {tolerancePercentage:F2}%)."
            : dimensionMismatch
                ? $"Visual check failed: Image dimensions differ (Baseline: {baseline.Width}x{baseline.Height}, Actual: {actual.Width}x{actual.Height}). {mismatchPercentage:F2}% diff."
                : $"Visual check failed: {mismatchPercentage:F2}% diff exceeds tolerance of {tolerancePercentage:F2}% ({mismatchedPixels:N0} mismatched pixels).";

        return new VisualComparisonResult
        {
            Passed = passed,
            TotalPixels = totalPixels,
            MismatchedPixels = mismatchedPixels,
            MismatchPercentage = mismatchPercentage,
            TolerancePercentage = tolerancePercentage,
            DimensionMismatch = dimensionMismatch,
            BaselineSize = (baseline.Width, baseline.Height),
            ActualSize = (actual.Width, actual.Height),
            DiffImage = diffImage,
            Message = message
        };
    }
}
