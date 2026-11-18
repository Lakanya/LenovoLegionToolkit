using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.Utils;
using NeoSmart.AsyncLock;

namespace LenovoLegionToolkit.Lib.PackageDownloader;

public class DriverUpdateChecker(
    SmartPackageDownloader smartPackageDownloader,
    PackageDownloaderSettings packageDownloaderSettings)
{
    private readonly AsyncLock _lock = new();
    private DateTime _lastCheckUtc = DateTime.MinValue;
    private List<Package>? _cachedPackages;

    public IReadOnlyList<Package>? CachedPackages => _cachedPackages;

    public IReadOnlyList<Package> AvailableUpdates =>
        _cachedPackages?
            .Where(p => p.IsUpdate && !packageDownloaderSettings.Store.HiddenPackages.Contains(p.Id))
            .OrderByDescending(p => p.IsBiosUpdate)
            .ThenByDescending(p => p.ReleaseDate)
            .ToList() ?? [];

    public async Task<IReadOnlyList<Package>> CheckAsync(bool force = false, CancellationToken token = default)
    {
        using (await _lock.LockAsync(token).ConfigureAwait(false))
        {
            if (!force && _cachedPackages is not null && (DateTime.UtcNow - _lastCheckUtc) < TimeSpan.FromHours(6))
                return AvailableUpdates;

            try
            {
                var mi = await Compatibility.GetMachineInformationAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(mi.MachineType) || mi.MachineType.Length != 4)
                    return [];

                var os = OSExtensions.GetCurrent();
                Log.Instance.Trace($"DriverUpdateChecker: checking driver/BIOS updates for {mi.MachineType} ({os})...");

                _cachedPackages = await smartPackageDownloader.GetPackagesAsync(mi.MachineType, os, null, token).ConfigureAwait(false);
                _lastCheckUtc = DateTime.UtcNow;

                var updates = AvailableUpdates;
                Log.Instance.Trace($"DriverUpdateChecker: found {updates.Count} applicable update(s) out of {_cachedPackages.Count} package(s).");
                return updates;
            }
            catch (Exception ex)
            {
                Log.Instance.Trace($"DriverUpdateChecker failed: {ex.Message}", ex);
                return AvailableUpdates;
            }
        }
    }

    public void UpdateCache(List<Package> packages)
    {
        _cachedPackages = packages;
        _lastCheckUtc = DateTime.UtcNow;
    }
}
