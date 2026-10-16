using System.Collections.Generic;

namespace PlaywrightMcpHarness.Studio.Models;

public class EdgeCaseCategoryOptions
{
    public bool IncludeHappyPath { get; set; } = true;
    public bool IncludeBoundaryLimits { get; set; } = true;
    public bool IncludeEmptyAndWhitespace { get; set; } = true;
    public bool IncludeSpecialCharsAndUnicode { get; set; } = true;
    public bool IncludeSecuritySanity { get; set; } = true;
    public bool IncludeMalformedFormats { get; set; } = true;
}

public class DetectedScenario
{
    public string Title { get; set; } = "";
    public string RawText { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public bool IsOutline { get; set; }
}

public class GenerateOutlineRequest
{
    public string ScenarioText { get; set; } = "";
    public string FeatureName { get; set; } = "";
    public EdgeCaseCategoryOptions Categories { get; set; } = new();
    public int MaxExamples { get; set; } = 8;
}

public class GenerateOutlineResponse
{
    public bool Success { get; set; }
    public string GeneratedOutline { get; set; } = "";
    public string? ErrorMessage { get; set; }
}
