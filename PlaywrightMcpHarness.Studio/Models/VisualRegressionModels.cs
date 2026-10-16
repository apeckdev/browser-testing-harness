using System;

namespace PlaywrightMcpHarness.Studio.Models;

public class VisualSnapshotItem
{
    public string SnapshotName { get; set; } = "";
    public string BaselineFileName { get; set; } = "";
    public string BaselineRelativePath { get; set; } = "";
    public string? BaselineUrl { get; set; }
    public string? ActualUrl { get; set; }
    public string? DiffUrl { get; set; }
    public bool HasBaseline { get; set; }
    public bool HasActual { get; set; }
    public bool HasDiff { get; set; }
    public double MismatchPercentage { get; set; }
    public (int Width, int Height)? Dimensions { get; set; }
    public DateTime LastModified { get; set; }
}

public class VisualDiffFinding
{
    public string SnapshotName { get; set; } = "";
    public double MismatchPercentage { get; set; }
    public double TolerancePercentage { get; set; }
    public string ActualFileName { get; set; } = "";
    public string DiffFileName { get; set; } = "";
    public string? ScenarioTitle { get; set; }
}
