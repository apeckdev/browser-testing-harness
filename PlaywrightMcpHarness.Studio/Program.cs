using PlaywrightMcpHarness.Studio.Components;
using PlaywrightMcpHarness.Studio.Models;
using PlaywrightMcpHarness.Studio.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Configure Harness settings
builder.Services.Configure<HarnessConfig>(builder.Configuration.GetSection(HarnessConfig.SectionName));

// Register AI & Test Harness services
builder.Services.AddSingleton<IChatClientFactory, ChatClientFactory>();
builder.Services.AddSingleton<IStepCatalogService, StepCatalogService>();
builder.Services.AddScoped<IMcpAgentService, McpAgentService>();
builder.Services.AddSingleton<ITestRunnerService, TestRunnerService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseStaticFiles();

// Serve test execution artifacts (screenshots, traces) from TestArtifacts directory
var testRunner = app.Services.GetRequiredService<ITestRunnerService>();
var artifactsPath = testRunner.GetTestArtifactsPath();
if (!Directory.Exists(artifactsPath))
{
    Directory.CreateDirectory(artifactsPath);
}
var tracesDir = Path.Combine(artifactsPath, "traces");
if (!Directory.Exists(tracesDir)) Directory.CreateDirectory(tracesDir);
var screenshotsDir = Path.Combine(artifactsPath, "screenshots");
if (!Directory.Exists(screenshotsDir)) Directory.CreateDirectory(screenshotsDir);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(artifactsPath),
    RequestPath = "/test-artifacts"
});

app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
