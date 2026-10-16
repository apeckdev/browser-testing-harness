using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlaywrightMcpHarness.Studio.Models;

namespace PlaywrightMcpHarness.Studio.Services;

public interface IVisualSnapshotService
{
    Task<List<VisualSnapshotItem>> GetVisualSnapshotsAsync(bool forceReload = false);
    Task<bool> AcceptNewBaselineAsync(string snapshotName);
    Task<bool> DeleteBaselineAsync(string snapshotName);
    string GetBaselinesDirectory();
}

public class VisualSnapshotService : IVisualSnapshotService
{
    private readonly HarnessConfig _config;
    private readonly ILogger<VisualSnapshotService> _logger;
    private List<VisualSnapshotItem> _cachedSnapshots = new();

    public VisualSnapshotService(IOptions<HarnessConfig> configOptions, ILogger<VisualSnapshotService> logger)
    {
        _config = configOptions.Value;
        _logger = logger;
    }

    public string GetBaselinesDirectory()
    {
        var testsRoot = GetTestsProjectRoot();
        var baselinesDir = Path.Combine(testsRoot, "VisualBaselines");
        if (!Directory.Exists(baselinesDir))
        {
            Directory.CreateDirectory(baselinesDir);
        }
        return baselinesDir;
    }

    public Task<List<VisualSnapshotItem>> GetVisualSnapshotsAsync(bool forceReload = false)
    {
        if (_cachedSnapshots.Count > 0 && !forceReload)
        {
            return Task.FromResult(_cachedSnapshots);
        }

        var results = new Dictionary<string, VisualSnapshotItem>(StringComparer.OrdinalIgnoreCase);

        var testsRoot = GetTestsProjectRoot();
        var baselinesDir = GetBaselinesDirectory();
        var visualArtifactsDir = Path.Combine(testsRoot, "TestArtifacts", "visual");

        // Auto-sync any baselines created in bin/ output directory to the source VisualBaselines/ directory
        var binBaselines = Path.Combine(testsRoot, "bin", "Debug", "net8.0", "VisualBaselines");
        if (Directory.Exists(binBaselines))
        {
            foreach (var binFile in Directory.GetFiles(binBaselines, "*.png"))
            {
                var targetFile = Path.Combine(baselinesDir, Path.GetFileName(binFile));
                if (!File.Exists(targetFile))
                {
                    try
                    {
                        File.Copy(binFile, targetFile, overwrite: true);
                        _logger.LogInformation("Auto-synced baseline {FileName} from bin output directory to source VisualBaselines", Path.GetFileName(binFile));
                    }
                    catch { }
                }
            }
        }

        // 1. Scan existing Golden Baselines
        if (Directory.Exists(baselinesDir))
        {
            var baselineFiles = Directory.GetFiles(baselinesDir, "*.png");
            foreach (var file in baselineFiles)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var fi = new FileInfo(file);

                results[name] = new VisualSnapshotItem
                {
                    SnapshotName = name,
                    BaselineFileName = Path.GetFileName(file),
                    BaselineRelativePath = Path.Combine("VisualBaselines", Path.GetFileName(file)),
                    BaselineUrl = $"/test-artifacts/visual-baselines/{Path.GetFileName(file)}?t={fi.LastWriteTimeUtc.Ticks}",
                    HasBaseline = true,
                    LastModified = fi.LastWriteTimeUtc
                };
            }
        }

        // 2. Scan recent Actual and Diff artifacts from TestArtifacts/visual
        if (Directory.Exists(visualArtifactsDir))
        {
            var artifactFiles = Directory.GetFiles(visualArtifactsDir, "*.png");
            foreach (var file in artifactFiles)
            {
                var fileName = Path.GetFileName(file);
                var fi = new FileInfo(file);

                if (fileName.EndsWith(".actual.png", StringComparison.OrdinalIgnoreCase))
                {
                    var name = fileName.Substring(0, fileName.Length - ".actual.png".Length);
                    if (!results.TryGetValue(name, out var item))
                    {
                        item = new VisualSnapshotItem
                        {
                            SnapshotName = name,
                            BaselineFileName = $"{name}.png",
                            BaselineRelativePath = Path.Combine("VisualBaselines", $"{name}.png"),
                            HasBaseline = false,
                            LastModified = fi.LastWriteTimeUtc
                        };
                        results[name] = item;
                    }

                    item.ActualUrl = $"/test-artifacts/visual/{fileName}?t={fi.LastWriteTimeUtc.Ticks}";
                    item.HasActual = true;
                }
                else if (fileName.EndsWith(".diff.png", StringComparison.OrdinalIgnoreCase))
                {
                    var name = fileName.Substring(0, fileName.Length - ".diff.png".Length);
                    if (!results.TryGetValue(name, out var item))
                    {
                        item = new VisualSnapshotItem
                        {
                            SnapshotName = name,
                            BaselineFileName = $"{name}.png",
                            BaselineRelativePath = Path.Combine("VisualBaselines", $"{name}.png"),
                            HasBaseline = false,
                            LastModified = fi.LastWriteTimeUtc
                        };
                        results[name] = item;
                    }

                    item.DiffUrl = $"/test-artifacts/visual/{fileName}?t={fi.LastWriteTimeUtc.Ticks}";
                    item.HasDiff = true;
                }
            }
        }

        _cachedSnapshots = results.Values.OrderByDescending(s => s.HasDiff).ThenBy(s => s.SnapshotName).ToList();
        return Task.FromResult(_cachedSnapshots);
    }

    public async Task<bool> AcceptNewBaselineAsync(string snapshotName)
    {
        try
        {
            var testsRoot = GetTestsProjectRoot();
            var baselinesDir = GetBaselinesDirectory();
            var visualArtifactsDir = Path.Combine(testsRoot, "TestArtifacts", "visual");

            var actualPath = Path.Combine(visualArtifactsDir, $"{snapshotName}.actual.png");
            if (!File.Exists(actualPath))
            {
                _logger.LogWarning("Cannot accept baseline: Actual image not found at {Path}", actualPath);
                return false;
            }

            var baselinePath = Path.Combine(baselinesDir, $"{snapshotName}.png");
            await Task.Run(() => File.Copy(actualPath, baselinePath, overwrite: true));

            // Clean up diff file since new baseline is accepted
            var diffPath = Path.Combine(visualArtifactsDir, $"{snapshotName}.diff.png");
            if (File.Exists(diffPath))
            {
                File.Delete(diffPath);
            }

            _logger.LogInformation("Promoted actual screenshot to golden baseline for '{SnapshotName}'", snapshotName);
            await GetVisualSnapshotsAsync(forceReload: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to accept new baseline for '{SnapshotName}'", snapshotName);
            return false;
        }
    }

    public async Task<bool> DeleteBaselineAsync(string snapshotName)
    {
        try
        {
            var baselinesDir = GetBaselinesDirectory();
            var baselinePath = Path.Combine(baselinesDir, $"{snapshotName}.png");
            if (File.Exists(baselinePath))
            {
                File.Delete(baselinePath);
            }

            await GetVisualSnapshotsAsync(forceReload: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete baseline for '{SnapshotName}'", snapshotName);
            return false;
        }
    }

    private string GetTestsProjectRoot()
    {
        var testsProjectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _config.TestsProjectPath));
        if (!Directory.Exists(testsProjectDir) && Directory.Exists(Path.GetFullPath(_config.TestsProjectPath)))
        {
            testsProjectDir = Path.GetFullPath(_config.TestsProjectPath);
        }
        return testsProjectDir;
    }
}
