using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Utils;
using Newtonsoft.Json.Linq;

namespace LenovoLegionToolkit.Lib.PackageDownloader;

public class NvidiaDriverPackageProvider(HttpClientFactory httpClientFactory)
{
    // GeForce RTX 50 Series (Notebooks) - RTX 5070 Laptop GPU (unified Game Ready DCH driver for all modern RTX notebooks)
    private const int DefaultPsid = 133;
    private const int DefaultPfid = 1077;

    public async Task<Package?> GetPackageAsync(OS os, CancellationToken token = default)
    {
        var installed = await Task.Run(GetInstalledNvidiaGpuInfo, token).ConfigureAwait(false);
        if (installed is null)
            return null;

        var (gpuName, installedVersionText, installedVersion) = installed.Value;
        var osId = os == OS.Windows10 ? 57 : 135;

        var url = $"https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
                  $"?func=DriverManualLookup&psid={DefaultPsid}&pfid={DefaultPfid}&osID={osId}" +
                  $"&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&ctk=null&sort1=0&numberOfResults=1";

        using var httpClient = httpClientFactory.Create();
        httpClient.Timeout = TimeSpan.FromSeconds(15);

        using var response = await httpClient.GetAsync(url, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        var root = JObject.Parse(json);
        var downloadInfo = root["IDS"]?[0]?["downloadInfo"];
        if (downloadInfo is null)
            return null;

        var latestVersionText = downloadInfo["Version"]?.Value<string>()?.Trim();
        var downloadUrl = downloadInfo["DownloadURL"]?.Value<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(latestVersionText) || string.IsNullOrWhiteSpace(downloadUrl))
            return null;

        var detailsUrl = downloadInfo["DetailsURL"]?.Value<string>()?.Trim();
        var fileSize = downloadInfo["DownloadURLFileSize"]?.Value<string>()?.Trim() ?? string.Empty;
        var releaseDateText = downloadInfo["ReleaseDateTime"]?.Value<string>()?.Trim();

        var releaseDate = DateTime.UtcNow.Date;
        if (!string.IsNullOrWhiteSpace(releaseDateText) &&
            DateTime.TryParse(releaseDateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
        {
            releaseDate = parsedDate;
        }

        var latestVersion = ParseSimpleVersion(latestVersionText);
        var isUpdate = installedVersion is not null && latestVersion is not null && latestVersion > installedVersion;

        var description = installedVersionText is not null
            ? isUpdate
                ? $"Installed: {installedVersionText}  ->  Latest WHQL: {latestVersionText}. Click Play to open NVCleanstall for a clean, debloated install."
                : $"Up to date (Installed: {installedVersionText}, Latest WHQL: {latestVersionText}). Click Play to open NVCleanstall."
            : $"Latest WHQL: {latestVersionText}. Click Play to open NVCleanstall for a clean, debloated install.";

        var fileName = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);

        return new Package
        {
            Id = $"nvidia-grd-{latestVersionText}",
            Title = $"NVIDIA GeForce Game Ready Driver ({gpuName})",
            Description = description,
            Version = latestVersionText,
            Category = "GPU Driver (NVIDIA / NVCleanstall)",
            FileName = fileName,
            FileSize = fileSize,
            ReleaseDate = releaseDate,
            Readme = string.IsNullOrWhiteSpace(detailsUrl) ? null : detailsUrl,
            FileLocation = downloadUrl,
            IsUpdate = isUpdate,
            Reboot = RebootType.NotRequired,
            IsNvidiaUpstream = true
        };
    }

    private static (string GpuName, string? VersionText, Version? Version)? GetInstalledNvidiaGpuInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, PNPDeviceID FROM Win32_VideoController");
            foreach (var obj in searcher.Get().OfType<ManagementObject>())
            {
                var pnpId = obj["PNPDeviceID"]?.ToString() ?? string.Empty;
                if (!pnpId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = obj["Name"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    name = "NVIDIA GeForce GPU";

                var rawDriverVersion = obj["DriverVersion"]?.ToString()?.Trim();
                var friendly = ConvertWindowsDriverVersionToNvidia(rawDriverVersion);
                return (name, friendly, ParseSimpleVersion(friendly));
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"NvidiaDriverPackageProvider: failed to query Win32_VideoController.", ex);
        }

        return null;
    }

    /// <summary>
    /// Converts Windows WDDM NVIDIA driver version (e.g. "32.0.16.1742" or "32.0.15.9186")
    /// to NVIDIA's public version string (e.g. "617.42" or "591.86").
    /// </summary>
    internal static string? ConvertWindowsDriverVersionToNvidia(string? windowsVersion)
    {
        if (string.IsNullOrWhiteSpace(windowsVersion))
            return null;

        var parts = windowsVersion.Split('.');
        if (parts.Length < 4)
            return null;

        var third = parts[2];
        var fourth = parts[3].PadLeft(4, '0');
        if (third.Length == 0 || fourth.Length < 4)
            return null;

        var fiveDigits = $"{third[^1]}{fourth[^4..]}";
        return $"{fiveDigits[..3]}.{fiveDigits[3..]}";
    }

    private static Version? ParseSimpleVersion(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText))
            return null;

        return Version.TryParse(versionText, out var v) ? v : null;
    }
}
