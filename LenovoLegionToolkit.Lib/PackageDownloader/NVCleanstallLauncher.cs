using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Utils;
using Microsoft.Win32;

namespace LenovoLegionToolkit.Lib.PackageDownloader;

public readonly record struct NVCleanstallLaunchResult(bool Success, bool InstalledViaWinget, string Message);

public static class NVCleanstallLauncher
{
    private const string TechPowerUpDownloadUrl = "https://www.techpowerup.com/download/techpowerup-nvcleanstall/";

    public static string? FindExecutablePath()
    {
        string[] candidatePaths =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVCleanstall", "NVCleanstall.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NVCleanstall", "NVCleanstall.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NVCleanstall", "NVCleanstall.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVCleanstall", "NVCleanstall.exe"),
        ];

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
                return path;
        }

        // Check standard Windows Uninstall registry keys for custom install locations
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var subPath in new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            })
            {
                try
                {
                    using var uninstallKey = root.OpenSubKey(subPath);
                    if (uninstallKey is null)
                        continue;

                    foreach (var name in uninstallKey.GetSubKeyNames())
                    {
                        using var appKey = uninstallKey.OpenSubKey(name);
                        var displayName = appKey?.GetValue("DisplayName")?.ToString();
                        if (displayName is null || !displayName.Contains("NVCleanstall", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var installLocation = appKey?.GetValue("InstallLocation")?.ToString();
                        if (!string.IsNullOrWhiteSpace(installLocation))
                        {
                            var exe = Path.Combine(installLocation.Trim('"'), "NVCleanstall.exe");
                            if (File.Exists(exe))
                                return exe;
                        }

                        var displayIcon = appKey?.GetValue("DisplayIcon")?.ToString()?.Trim('"');
                        if (!string.IsNullOrWhiteSpace(displayIcon) &&
                            displayIcon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            File.Exists(displayIcon))
                        {
                            return displayIcon;
                        }
                    }
                }
                catch
                {
                    // Ignore registry access errors
                }
            }
        }

        return null;
    }

    public static async Task<NVCleanstallLaunchResult> LaunchOrInstallAsync(CancellationToken token = default)
    {
        var exePath = FindExecutablePath();
        var installedViaWinget = false;

        if (exePath is null)
        {
            Log.Instance.Trace($"NVCleanstall not found locally. Attempting installation via winget...");

            try
            {
                using var winget = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "winget",
                        Arguments = "install --id TechPowerUp.NVCleanstall --exact --accept-source-agreements --accept-package-agreements",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                winget.Start();
                await winget.WaitForExitAsync(token).ConfigureAwait(false);

                if (winget.ExitCode == 0)
                {
                    installedViaWinget = true;
                    exePath = FindExecutablePath();
                }
                else
                {
                    Log.Instance.Trace($"winget install TechPowerUp.NVCleanstall exited with code {winget.ExitCode}.");
                }
            }
            catch (Exception ex)
            {
                Log.Instance.Trace($"Failed to install NVCleanstall via winget.", ex);
            }
        }

        if (exePath is null)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = TechPowerUpDownloadUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log.Instance.Trace($"Failed to open NVCleanstall download page.", ex);
            }

            return new NVCleanstallLaunchResult(
                false,
                false,
                "NVCleanstall was not found and winget auto-install did not complete. Opened TechPowerUp download page.");
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                UseShellExecute = true
            });

            var msg = installedViaWinget
                ? "Installed and launched NVCleanstall. Select the latest driver and your preferred tweaks to install."
                : "Launched NVCleanstall. Select the latest driver and your preferred tweaks to install.";

            return new NVCleanstallLaunchResult(true, installedViaWinget, msg);
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to launch NVCleanstall from '{exePath}'.", ex);
            return new NVCleanstallLaunchResult(false, installedViaWinget, $"Failed to launch NVCleanstall: {ex.Message}");
        }
    }
}
