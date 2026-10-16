using System;
using System.Collections.Generic;

namespace PlaywrightMcpHarness.Studio.Models;

public enum LocatorHealthTier
{
    Resilient,
    Caution,
    Fragile
}

public class LocatorFinding
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceFile { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public int LineNumber { get; set; }
    public string MethodName { get; set; } = "";
    public string StepAttribute { get; set; } = "";
    public string RawExpression { get; set; } = "";
    public string LocatorType { get; set; } = "";
    public LocatorHealthTier HealthTier { get; set; } = LocatorHealthTier.Resilient;
    public int HealthScore { get; set; } = 100;
    public string IssueReason { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public string FullMethodSource { get; set; } = "";
}

public class LocatorHealthReport
{
    public int OverallScore { get; set; } = 100;
    public string HealthGrade { get; set; } = "A+";
    public int TotalLocators { get; set; }
    public int ResilientCount { get; set; }
    public int CautionCount { get; set; }
    public int FragileCount { get; set; }
    public List<LocatorFinding> Findings { get; set; } = new();
    public DateTime ScannedAt { get; set; } = DateTime.UtcNow;
}

public class AutoHealRequest
{
    public LocatorFinding Finding { get; set; } = new();
    public string? UserGuidance { get; set; }
}

public class AutoHealResponse
{
    public bool Success { get; set; }
    public string OriginalMethodCode { get; set; } = "";
    public string RefactoredMethodCode { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string? ErrorMessage { get; set; }
}
