using System;
using System.Collections.Generic;

namespace PlaywrightMcpHarness.Studio.Models;

public class PageObjectInfo
{
    public string ClassName { get; set; } = "";
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Content { get; set; } = "";
    public List<PageObjectMethodInfo> Methods { get; set; } = new();
    public List<string> Locators { get; set; } = new();
}

public class PageObjectMethodInfo
{
    public string MethodName { get; set; } = "";
    public string Signature { get; set; } = "";
    public string ReturnType { get; set; } = "Task";
    public List<string> Parameters { get; set; } = new();
    public string Summary { get; set; } = "";
    public string MethodBody { get; set; } = "";
}

public class ExtractPageObjectRequest
{
    public string SourceStepDefinitionFile { get; set; } = "";
    public string SourceStepDefinitionCode { get; set; } = "";
    public string SuggestedPageObjectName { get; set; } = "";
    public string? UserGuidance { get; set; }
}

public class ExtractPageObjectResponse
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string PageObjectClassName { get; set; } = "";
    public string PageObjectFileName { get; set; } = "";
    public string GeneratedPageObjectCode { get; set; } = "";
    public string RefactoredStepDefinitionCode { get; set; } = "";
    public string Explanation { get; set; } = "";
}

public class PageObjectCatalogReport
{
    public int TotalPageObjects { get; set; }
    public int TotalMethods { get; set; }
    public List<PageObjectInfo> PageObjects { get; set; } = new();
}
