using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.PackageDownloader.Detectors;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.PackageDownloader;

public partial class SmartPackageDownloader(
    HttpClientFactory httpClientFactory,
    PCSupportPackageDownloader pcSupportPackageDownloader,
    VantagePackageDownloader vantagePackageDownloader,
    NvidiaDriverPackageProvider nvidiaDriverPackageProvider)
    : AbstractPackageDownloader(httpClientFactory)
{
    [GeneratedRegex(@"\s+for\s+windows\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex OsSuffixRegex();

    [GeneratedRegex(@"\s*-\s*legion\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex ModelSuffixRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphaNumRegex();

    public override async Task<List<Package>> GetPackagesAsync(string machineType, OS os, IProgress<float>? progress = null, CancellationToken token = default)
    {
        progress?.Report(0);

        var pcSupportTask = SafeGetAsync(() => pcSupportPackageDownloader.GetPackagesAsync(machineType, os, null, token));
        var vantageProgress = progress is null ? null : new Progress<float>(v => progress.Report(Math.Min(95f, v * 0.95f)));
        var vantageTask = SafeGetAsync(() => vantagePackageDownloader.GetPackagesAsync(machineType, os, vantageProgress, token));
        var nvidiaTask = SafeGetSingleAsync(() => nvidiaDriverPackageProvider.GetPackageAsync(os, token));

        await Task.WhenAll(pcSupportTask, vantageTask, nvidiaTask).ConfigureAwait(false);

        var pcSupportPackages = await pcSupportTask.ConfigureAwait(false);
        var vantagePackages = await vantageTask.ConfigureAwait(false);
        var nvidiaPackage = await nvidiaTask.ConfigureAwait(false);

        var hasUpstreamNvidia = nvidiaPackage is not null;

        // First collapse Vantage multi-OS duplicates (e.g. Win11 24H2 vs 25H2 for the same peripheral)
        var dedupedVantage = DeduplicatePackages(
            vantagePackages
                .Where(v => IsValidUrl(v.FileLocation))
                .Where(v => !(hasUpstreamNvidia && IsOemNvidiaDriver(v)))
                .Select(v => new Package
                {
                    Id = v.Id,
                    Title = v.Title,
                    Description = v.Description,
                    Version = v.Version,
                    Category = v.Category,
                    FileName = v.FileName,
                    FileSize = v.FileSize,
                    FileCrc = v.FileCrc,
                    ReleaseDate = v.ReleaseDate,
                    Readme = v.Readme,
                    FileLocation = v.FileLocation,
                    IsUpdate = v.IsUpdate,
                    Reboot = v.Reboot,
                    InstallCommand = v.InstallCommand,
                    InstalledVersion = v.InstalledVersion,
                    SourceName = "Lenovo Vantage",
                    IsUtilityOrTool = PCSupportPackageUpdateDetector.IsUtilityCategory(v.Category, v.Title)
                }));

        var vantageByFile = dedupedVantage
            .Where(p => !string.IsNullOrWhiteSpace(p.FileName))
            .GroupBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.ReleaseDate).First(), StringComparer.OrdinalIgnoreCase);

        var vantageByKey = dedupedVantage
            .GroupBy(GetCanonicalDriverKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.ReleaseDate).First(), StringComparer.OrdinalIgnoreCase);

        var merged = new List<Package>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // PC Support packages are the primary source (newest versions, accurate BIOS, clean descriptions)
        var dedupedPcSupport = DeduplicatePackages(
            pcSupportPackages.Where(p => !(hasUpstreamNvidia && IsOemNvidiaDriver(p))));

        foreach (var pcPkg in dedupedPcSupport)
        {
            var key = GetCanonicalDriverKey(pcPkg);
            seenFiles.Add(pcPkg.FileName);
            seenKeys.Add(key);

            Package? matchedVantage = null;
            if (vantageByFile.TryGetValue(pcPkg.FileName, out var byFile))
                matchedVantage = byFile;
            else if (vantageByKey.TryGetValue(key, out var byKey))
                matchedVantage = byKey;

            if (matchedVantage is { } vPkg)
            {
                seenFiles.Add(vPkg.FileName);
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
                    IsUpdate = pcPkg.IsUpdate || (vPkg.IsUpdate && vPkg.ReleaseDate >= pcPkg.ReleaseDate),
                    Reboot = pcPkg.Reboot != RebootType.NotRequired ? pcPkg.Reboot : vPkg.Reboot,
                    InstallCommand = vPkg.InstallCommand,
                    InstalledVersion = pcPkg.InstalledVersion ?? vPkg.InstalledVersion,
                    InstalledDate = pcPkg.InstalledDate ?? vPkg.InstalledDate,
                    MatchedAvailableVersion = pcPkg.MatchedAvailableVersion ?? vPkg.MatchedAvailableVersion,
                    IsInstalledNewer = pcPkg.IsInstalledNewer,
                    SourceName = pcPkg.SourceName ?? "Lenovo PC Support",
                    IsUtilityOrTool = pcPkg.IsUtilityOrTool
                });
            }
            else
            {
                merged.Add(pcPkg);
            }
        }

        // Only add Vantage-only packages if no PC Support entry already covers that peripheral
        foreach (var vPkg in dedupedVantage)
        {
            var key = GetCanonicalDriverKey(vPkg);
            if (seenFiles.Contains(vPkg.FileName) || seenKeys.Contains(key))
                continue;

            seenFiles.Add(vPkg.FileName);
            seenKeys.Add(key);
            merged.Add(vPkg);
        }

        if (nvidiaPackage is { } nvPkg)
            merged.Insert(0, nvPkg);

        progress?.Report(100);
        return merged;
    }

    /// <summary>
    /// Collapses duplicate entries for the same peripheral (e.g. Win11 24H2 vs 25H2 builds)
    /// by keeping only the newest release per canonical driver key.
    /// </summary>
    private static List<Package> DeduplicatePackages(IEnumerable<Package> packages)
    {
        return packages
            .GroupBy(GetCanonicalDriverKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(p => p.ReleaseDate)
                .ThenByDescending(p => p.IsUpdate)
                .First())
            .ToList();
    }

    /// <summary>
    /// Builds a stable per-peripheral identity key so multi-OS variants and PC Support / Vantage
    /// pairs for the same hardware device collapse into a single clean entry.
    /// </summary>
    private static string GetCanonicalDriverKey(Package pkg)
    {
        if (pkg.IsNvidiaUpstream)
            return "upstream:nvidia";

        if (pkg.IsBiosUpdate && !pkg.Title.Contains("VBIOS", StringComparison.OrdinalIgnoreCase) && !pkg.Title.Contains("Tool", StringComparison.OrdinalIgnoreCase))
            return "driver:bios";

        var combined = $"{pkg.Category} {pkg.Title}";

        if (!pkg.IsUtilityOrTool)
        {
            if (combined.Contains("Camera", StringComparison.OrdinalIgnoreCase)) return "driver:camera";
            if (combined.Contains("Realtek Audio", StringComparison.OrdinalIgnoreCase) || pkg.Category.Equals("Audio", StringComparison.OrdinalIgnoreCase)) return "driver:audio";
            if (combined.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) return "driver:bluetooth";
            if (combined.Contains("WLAN", StringComparison.OrdinalIgnoreCase) || combined.Contains("Wireless LAN", StringComparison.OrdinalIgnoreCase)) return "driver:wlan";
            if (combined.Contains("LAN", StringComparison.OrdinalIgnoreCase) || combined.Contains("Ethernet", StringComparison.OrdinalIgnoreCase)) return "driver:lan";
            if (combined.Contains("AMD VGA", StringComparison.OrdinalIgnoreCase) || (combined.Contains("Display", StringComparison.OrdinalIgnoreCase) && combined.Contains("AMD", StringComparison.OrdinalIgnoreCase))) return "driver:amd-vga";
            if (combined.Contains("Energy Management", StringComparison.OrdinalIgnoreCase)) return "driver:energy-management";
            if (combined.Contains("Fn and Function Keys", StringComparison.OrdinalIgnoreCase) || combined.Contains("Hotkeys", StringComparison.OrdinalIgnoreCase)) return "driver:fn-keys";
            if (combined.Contains("IGRS", StringComparison.OrdinalIgnoreCase) || combined.Contains("RFI Suppression", StringComparison.OrdinalIgnoreCase)) return "driver:igrs";
            if (combined.Contains("Serial IO", StringComparison.OrdinalIgnoreCase)) return "driver:amd-sio";
            if (combined.Contains("DRTM", StringComparison.OrdinalIgnoreCase)) return "driver:amd-drtm";
            if (combined.Contains("VBIOS", StringComparison.OrdinalIgnoreCase)) return "driver:vbios";
        }

        // Fallback: check Lenovo 16-char package stem suffix (e.g. yqy5010f9123h1j0 vs yqy5020f9123h1j0 -> 9123h1j0)
        var stem = Path.GetFileNameWithoutExtension(pkg.FileName ?? string.Empty);
        if (stem.Length == 16 && char.IsDigit(stem[5]))
            return $"stem:{stem[8..].ToLowerInvariant()}";

        // Fallback: normalized title with OS & model suffixes stripped
        var title = OsSuffixRegex().Replace(pkg.Title ?? string.Empty, string.Empty);
        title = ModelSuffixRegex().Replace(title, string.Empty);
        var normalized = NonAlphaNumRegex().Replace(title.ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrEmpty(normalized) ? $"file:{pkg.FileName}" : $"title:{normalized}";
    }

    private static bool IsValidUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static bool IsOemNvidiaDriver(Package pkg) =>
        !pkg.IsNvidiaUpstream &&
        !pkg.IsBiosUpdate &&
        pkg.Title.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) &&
        (pkg.Title.Contains("VGA", StringComparison.OrdinalIgnoreCase) ||
         pkg.Title.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ||
         pkg.Title.Contains("Display", StringComparison.OrdinalIgnoreCase) ||
         pkg.Category.Contains("Display", StringComparison.OrdinalIgnoreCase));

    private static async Task<Package?> SafeGetSingleAsync(Func<Task<Package?>> getter)
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
            Log.Instance.Trace($"SmartPackageDownloader upstream fetch failed: {ex.Message}", ex);
            return null;
        }
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
