using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BoDi;
using Microsoft.Playwright;
using TechTalk.SpecFlow;

namespace PlaywrightMcpHarness.Tests.Hooks;

[Binding]
public class PlaywrightHooks
{
    private readonly IObjectContainer _container;
    private readonly ScenarioContext _scenarioContext;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;

    public PlaywrightHooks(IObjectContainer container, ScenarioContext scenarioContext)
    {
        _container = container;
        _scenarioContext = scenarioContext;
    }

    [BeforeScenario]
    public async Task BeforeScenarioAsync()
    {
        _playwright = await Playwright.CreateAsync();

        var headedEnv = Environment.GetEnvironmentVariable("HEADED");
        var pwDebugEnv = Environment.GetEnvironmentVariable("PWDEBUG");
        bool isHeaded = headedEnv == "1" || pwDebugEnv == "1";

        float slowMo = 0;
        var slowMoEnv = Environment.GetEnvironmentVariable("SLOWMO");
        if (!string.IsNullOrEmpty(slowMoEnv) && float.TryParse(slowMoEnv, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedSlowMo))
        {
            slowMo = parsedSlowMo;
        }

        Console.WriteLine($"[HOOK_LOG] isHeaded={isHeaded} (HEADED='{headedEnv}'), slowMo={slowMo} (SLOWMO='{slowMoEnv}')");

        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = !isHeaded,
            SlowMo = slowMo,
            Args = new[] { "--no-sandbox", "--hide-scrollbars" }
        });

        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
        });

        await _context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true
        });

        _page = await _context.NewPageAsync();
        if (isHeaded)
        {
            await _page.BringToFrontAsync();
        }
        _container.RegisterInstanceAs<IPage>(_page);
    }

    [AfterScenario]
    public async Task AfterScenarioAsync()
    {
        var scenarioTitle = _scenarioContext.ScenarioInfo.Title ?? "scenario";
        var sanitizedTitle = Regex.Replace(scenarioTitle, @"[^a-zA-Z0-9_\-]", "_");
        
        var artifactsDir = Environment.GetEnvironmentVariable("TEST_ARTIFACTS_DIR");
        if (string.IsNullOrWhiteSpace(artifactsDir))
        {
            artifactsDir = Path.Combine(Directory.GetCurrentDirectory(), "TestArtifacts");
        }

        // 1. Capture Failure Screenshot if test encountered an error
        bool isFailed = _scenarioContext.TestError != null || _scenarioContext.ScenarioExecutionStatus != ScenarioExecutionStatus.OK;
        if (isFailed && _page != null)
        {
            try
            {
                var screenshotsDir = Path.Combine(artifactsDir, "screenshots");
                if (!Directory.Exists(screenshotsDir))
                {
                    Directory.CreateDirectory(screenshotsDir);
                }

                var screenshotPath = Path.Combine(screenshotsDir, $"{sanitizedTitle}.png");
                await _page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = screenshotPath,
                    FullPage = true
                });
                Console.WriteLine($"[ARTIFACT_SCREENSHOT] {sanitizedTitle}.png");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlaywrightHooks] Error capturing failure screenshot: {ex.Message}");
            }
        }

        // 2. Stop Tracing and save trace .zip
        try
        {
            if (_context != null)
            {
                var tracesDir = Path.Combine(artifactsDir, "traces");
                if (!Directory.Exists(tracesDir))
                {
                    Directory.CreateDirectory(tracesDir);
                }

                var tracePath = Path.Combine(tracesDir, $"{sanitizedTitle}.zip");
                await _context.Tracing.StopAsync(new TracingStopOptions
                {
                    Path = tracePath
                });
                Console.WriteLine($"[ARTIFACT_TRACE] {sanitizedTitle}.zip");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PlaywrightHooks] Error saving trace: {ex.Message}");
        }
        finally
        {
            var headedEnv = Environment.GetEnvironmentVariable("HEADED");
            var slowMoEnv = Environment.GetEnvironmentVariable("SLOWMO");
            if (headedEnv == "1" && !string.IsNullOrEmpty(slowMoEnv) && float.TryParse(slowMoEnv, out var sm) && sm > 0)
            {
                // Brief pause so the user can clearly observe the finished scenario on screen
                await Task.Delay(1200);
            }

            if (_page != null)
            {
                await _page.CloseAsync();
            }

            if (_context != null)
            {
                await _context.CloseAsync();
            }

            if (_browser != null)
            {
                await _browser.CloseAsync();
            }

            _playwright?.Dispose();
        }
    }
}
