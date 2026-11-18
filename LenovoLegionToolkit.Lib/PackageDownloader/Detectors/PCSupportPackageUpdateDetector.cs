using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.System.Management;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.PackageDownloader.Detectors;

internal partial class PCSupportPackageUpdateDetector
{
    [GeneratedRegex(@"^([A-Z0-9]{4})(\d{2})WW$", RegexOptions.IgnoreCase)]
    private static partial Regex BiosVersionRegex();

    [GeneratedRegex(@"\b(\d+\.\d+\.\d+\.\d+)\b")]
    private static partial Regex DottedVersionRegex();

    private readonly List<DriverInfo> _driverInfoCache = [];
    private BiosVersion? _currentBios;

    public async Task InitializeAsync()
    {
        var miTask = Compatibility.GetMachineInformationAsync();
        var driversTask = WMI.Win32.PnpSignedDriver.ReadAsync();
        await Task.WhenAll(miTask, driversTask).ConfigureAwait(false);

        _currentBios = (await miTask.ConfigureAwait(false)).BiosVersion;
        _driverInfoCache.Clear();
        _driverInfoCache.AddRange(await driversTask.ConfigureAwait(false));
    }

    public (bool IsUpdate, RebootType Reboot) Detect(string category, string title, string versionString, DateTime releaseDate)
    {
        if (string.IsNullOrWhiteSpace(versionString))
            return (false, RebootType.NotRequired);

        // 1. BIOS update check (e.g. RYCN30WW vs installed RYCN29WW)
        var biosMatch = BiosVersionRegex().Match(versionString.Trim());
        if (biosMatch.Success && _currentBios.HasValue)
        {
            var prefix = biosMatch.Groups[1].Value.ToUpperInvariant();
            if (int.TryParse(biosMatch.Groups[2].Value, out var pkgBiosNum))
            {
                var pkgBios = new BiosVersion(prefix, pkgBiosNum);
                if (_currentBios.Value.IsLowerThan(pkgBios))
                    return (true, RebootType.Forced);
            }

            return (false, RebootType.Forced);
        }

        // Skip pure diagnostic tools / apps / manuals
        if (category.Contains("Diagnostic", StringComparison.OrdinalIgnoreCase) ||
            category.Equals("Tool", StringComparison.OrdinalIgnoreCase) ||
            category.Contains("Software and Utilities", StringComparison.OrdinalIgnoreCase))
        {
            return (false, RebootType.NotRequired);
        }

        // 2. Driver version comparison against installed PnP drivers
        var extractedVersions = DottedVersionRegex().Matches(versionString)
            .Select(m => Version.TryParse(m.Groups[1].Value, out var v) ? v : null)
            .Where(v => v is not null)
            .Select(v => v!)
            .ToArray();

        if (extractedVersions.Length == 0 || _driverInfoCache.Count == 0)
            return (false, RebootType.NotRequired);

        var candidateDrivers = FilterDriversForPackage(category, title);
        if (candidateDrivers.Count == 0)
            return (false, RebootType.NotRequired);

        foreach (var pkgVer in extractedVersions)
        {
            // Match installed driver sharing the same Major.Minor branch so multi-chip bundles
            // only compare against the hardware chip family actually present on this machine.
            var matchingInstalled = candidateDrivers
                .Where(d => d.Version is not null && d.Version.Major == pkgVer.Major && d.Version.Minor == pkgVer.Minor)
                .ToArray();

            if (matchingInstalled.Length == 0)
                continue;

            var anyOutdated = matchingInstalled.Any(d =>
                d.Version! < pkgVer &&
                (d.Date is null || releaseDate.Date >= d.Date.Value.Date));

            var anyAlreadyNewerOrEqual = matchingInstalled.Any(d => d.Version! >= pkgVer);

            if (anyOutdated && !anyAlreadyNewerOrEqual)
                return (true, RebootType.Requested);
        }

        return (false, RebootType.NotRequired);
    }

    private List<DriverInfo> FilterDriversForPackage(string category, string title)
    {
        var combined = $"{category} {title}";

        if (combined.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return _driverInfoCache.Where(d => ContainsId(d, "VEN_10DE")).ToList();

        if (combined.Contains("AMD VGA", StringComparison.OrdinalIgnoreCase) ||
            (combined.Contains("Display", StringComparison.OrdinalIgnoreCase) && combined.Contains("AMD", StringComparison.OrdinalIgnoreCase)))
            return _driverInfoCache.Where(d => ContainsId(d, "VEN_1002")).ToList();

        if (combined.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
            return _driverInfoCache.Where(d => ContainsId(d, "USB\\VID_0E8D", "USB\\VID_0489", "USB\\VID_13D3", "USB\\VID_0BDA", "USB\\VID_8087")).ToList();

        if (combined.Contains("WLAN", StringComparison.OrdinalIgnoreCase) || combined.Contains("Wireless", StringComparison.OrdinalIgnoreCase))
            return _driverInfoCache.Where(d => ContainsId(d, "VEN_14C3", "VEN_10EC", "VEN_8086", "VEN_17CB")).ToList();

        if (combined.Contains("LAN", StringComparison.OrdinalIgnoreCase) || combined.Contains("Ethernet", StringComparison.OrdinalIgnoreCase))
            return _driverInfoCache.Where(d => ContainsId(d, "VEN_10EC&DEV_81", "VEN_10EC&DEV_26", "VEN_8086")).ToList();

        if (combined.Contains("Energy Management", StringComparison.OrdinalIgnoreCase))
            return _driverInfoCache.Where(d => ContainsId(d, "VPC2004")).ToList();

        if (combined.Contains("Fn and Function Keys", StringComparison.OrdinalIgnoreCase))
            return _driverInfoCache.Where(d => ContainsId(d, "LEN0100", "LEN2004", "FNKEY")).ToList();

        return [];
    }

    private static bool ContainsId(DriverInfo driver, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (driver.DeviceId.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                driver.HardwareId.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
