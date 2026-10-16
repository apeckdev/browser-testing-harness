using System;
using System.Collections.Generic;

namespace PlaywrightMcpHarness.Studio.Models;

public class StepDefinitionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string StepType { get; set; } = "Given"; // "Given", "When", "Then", "StepDefinition"
    public string Pattern { get; set; } = ""; // Original pattern in attribute
    public string NormalizedPattern { get; set; } = ""; // e.g. "I am on the \"{pageName}\" page"
    public string AutocompleteSnippet { get; set; } = ""; // Insertable template e.g. "I am on the \"\" page"
    public string MethodName { get; set; } = "";
    public List<StepParameterInfo> Parameters { get; set; } = new();
    public string SourceFile { get; set; } = "";
    public int LineNumber { get; set; }

    public string FullDisplay => $"{StepType} {NormalizedPattern}";
}

public class StepParameterInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
}
