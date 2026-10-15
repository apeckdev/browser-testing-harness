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
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
