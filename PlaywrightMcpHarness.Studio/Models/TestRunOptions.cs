using System;

namespace PlaywrightMcpHarness.Studio.Models;

public class TestRunOptions
{
    public VisualExecutionMode Mode { get; set; } = VisualExecutionMode.HeadedSlowMo;
    public bool RunInParallel { get; set; } = false;
    public string TargetScope { get; set; } = "all"; // "all" or "selected"
    public string? SelectedFilePath { get; set; }
}
