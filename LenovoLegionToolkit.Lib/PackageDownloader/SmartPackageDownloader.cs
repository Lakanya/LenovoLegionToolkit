using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.PackageDownloader;

public class SmartPackageDownloader(
    HttpClientFactory httpClientFactory,
    PCSupportPackageDownloader pcSupportPackageDownloader,
    VantagePackageDownloader vantagePackageDownloader)
    : AbstractPackageDownloader(httpClientFactory)
{
    public override async Task<List<Package>> GetPackagesAsync(string machineType, OS os, IProgress<float>? progress = null, CancellationToken token = default)
    {
        progress?.Report(0);

        var pcSupportTask = SafeGetAsync(() => pcSupportPackageDownloader.GetPackagesAsync(machineType, os, null, token));
        var vantageProgress = progress is null ? null : new Progress<float>(v => progress.Report(Math.Min(95f, v * 0.95f)));
        var vantageTask = SafeGetAsync(() => vantagePackageDownloader.GetPackagesAsync(machineType, os, vantageProgress, token));

        await Task.WhenAll(pcSupportTask, vantageTask).ConfigureAwait(false);

        var pcSupportPackages = await pcSupportTask.ConfigureAwait(false);
        var vantagePackages = await vantageTask.ConfigureAwait(false);

        var vantageByFile = vantagePackages
            .Where(p => !string.IsNullOrWhiteSpace(p.FileName))
            .GroupBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.ReleaseDate).First(), StringComparer.OrdinalIgnoreCase);

        var merged = new List<Package>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pcPkg in pcSupportPackages)
        {
            seenFiles.Add(pcPkg.FileName);

            if (vantageByFile.TryGetValue(pcPkg.FileName, out var vPkg))
            {
                merged.Add(new Package
                {
                    Id = pcPkg.Id,
                    Title = pcPkg.Title,
                    Description = pcPkg.Description,
                    Version = pcPkg.Version,
                    Category = pcPkg.Category,
                    FileName = pcPkg.FileName,
                    FileSize = pcPkg.FileSize,
                    FileCrc = !string.IsNullOrEmpty(pcPkg.FileCrc) ? pcPkg.FileCrc : vPkg.FileCrc,
                    ReleaseDate = pcPkg.ReleaseDate >= vPkg.ReleaseDate ? pcPkg.ReleaseDate : vPkg.ReleaseDate,
                    Readme = pcPkg.Readme ?? vPkg.Readme,
                    FileLocation = pcPkg.FileLocation,
                    IsUpdate = vPkg.IsUpdate || pcPkg.IsUpdate,
                    Reboot = vPkg.Reboot != RebootType.NotRequired ? vPkg.Reboot : pcPkg.Reboot,
                    InstallCommand = vPkg.InstallCommand
                });
            }
            else
            {
                merged.Add(pcPkg);
            }
        }

        var hasNewerBiosInMerged = merged.Any(p => p.Category.Contains("BIOS", StringComparison.OrdinalIgnoreCase));

        foreach (var vPkg in vantagePackages)
        {
            if (seenFiles.Contains(vPkg.FileName))
                continue;

            // Drop stale Vantage BIOS entry when PCSupport already provides the newer BIOS package
            if (hasNewerBiosInMerged && vPkg.Category.Contains("BIOS", StringComparison.OrdinalIgnoreCase))
            {
                var latestBiosDate = merged
                    .Where(p => p.Category.Contains("BIOS", StringComparison.OrdinalIgnoreCase))
                    .Max(p => p.ReleaseDate);
                if (vPkg.ReleaseDate < latestBiosDate)
                    continue;
            }

            seenFiles.Add(vPkg.FileName);
            merged.Add(vPkg);
        }

        progress?.Report(100);
        return merged;
    }

    private static async Task<List<Package>> SafeGetAsync(Func<Task<List<Package>>> getter)
    {
        try
        {
            return await getter().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"SmartPackageDownloader source fetch failed: {ex.Message}", ex);
            return [];
        }
    }
}
