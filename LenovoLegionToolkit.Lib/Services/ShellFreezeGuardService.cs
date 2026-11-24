using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Utils;
using Microsoft.Win32;

namespace LenovoLegionToolkit.Lib.Services;

public class ShellFreezeGuardService
{
    private const string BlockedKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";
    private const string CachedKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Cached";
    private const string DirectXUserGpuPrefsPath = @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences";

    private static readonly IReadOnlyDictionary<string, string> FreezeProneShellExtensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // AMD Radeon Software packaged Explorer context menu (atiacm64.dll) - stalls 15s+ when dGPU is in D3cold
        ["{6767B3BC-8FF7-11EC-B909-0242AC120002}"] = "Blocked by Lenovo Legion Toolkit (AMD Radeon Software Explorer freeze fix)",
        // NVIDIA Desktop Context Menu - wakes sleeping dGPU on right-click
        ["{3D1975AF-48C6-4f8e-A182-BE0E08FA86A9}"] = "Blocked by Lenovo Legion Toolkit (NVIDIA Desktop Context Menu freeze fix)"
    };

    public bool IsApplied()
    {
        try
        {
            using var blockedKey = Registry.CurrentUser.OpenSubKey(BlockedKeyPath, writable: false);
            if (blockedKey is null)
                return false;

            foreach (var clsid in FreezeProneShellExtensions.Keys)
            {
                if (blockedKey.GetValue(clsid) is null)
                    return false;
            }

            if (Environment.ProcessPath is { Length: > 0 } exePath)
            {
                using var gpuPrefsKey = Registry.CurrentUser.OpenSubKey(DirectXUserGpuPrefsPath, writable: false);
                var val = gpuPrefsKey?.GetValue(exePath)?.ToString();
                if (val is null || !val.Contains("GpuPreference=1;", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"ShellFreezeGuardService.IsApplied failed.", ex);
            return false;
        }
    }

    /// <summary>
    /// Ensures freeze-prone AMD &amp; NVIDIA Explorer shell extensions remain blocked,
    /// purges stale Shell Extensions\Cached entries for them, and pins LLT to the iGPU.
    /// Returns true if any registry value was added or repaired.
    /// </summary>
    public bool Apply()
    {
        var changed = false;

        try
        {
            using var blockedKey = Registry.CurrentUser.CreateSubKey(BlockedKeyPath, writable: true);
            if (blockedKey is not null)
            {
                foreach (var (clsid, reason) in FreezeProneShellExtensions)
                {
                    if (blockedKey.GetValue(clsid) is null)
                    {
                        blockedKey.SetValue(clsid, reason, RegistryValueKind.String);
                        changed = true;
                    }
                }
            }

            // Purge stale cached shell extension states for blocked CLSIDs so Explorer stops invoking them
            using var cachedKey = Registry.CurrentUser.OpenSubKey(CachedKeyPath, writable: true);
            if (cachedKey is not null)
            {
                foreach (var valueName in cachedKey.GetValueNames())
                {
                    foreach (var clsid in FreezeProneShellExtensions.Keys)
                    {
                        if (valueName.Contains(clsid, StringComparison.OrdinalIgnoreCase))
                        {
                            cachedKey.DeleteValue(valueName, throwOnMissingValue: false);
                            changed = true;
                            break;
                        }
                    }
                }
            }

            // Pin LLT itself to iGPU (GpuPreference=1;) so opening LLT never wakes the dGPU
            if (Environment.ProcessPath is { Length: > 0 } exePath)
            {
                using var gpuPrefsKey = Registry.CurrentUser.CreateSubKey(DirectXUserGpuPrefsPath, writable: true);
                if (gpuPrefsKey is not null)
                {
                    var current = gpuPrefsKey.GetValue(exePath)?.ToString();
                    if (current is null || !current.Contains("GpuPreference=1;", StringComparison.OrdinalIgnoreCase))
                    {
                        gpuPrefsKey.SetValue(exePath, "GpuPreference=1;", RegistryValueKind.String);
                        changed = true;
                    }
                }
            }

            if (changed)
                Log.Instance.Trace($"ShellFreezeGuardService: applied/repaired Explorer shell extension blocks and iGPU pin.");
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"ShellFreezeGuardService.Apply failed.", ex);
        }

        return changed;
    }

    public void Revert()
    {
        try
        {
            using var blockedKey = Registry.CurrentUser.OpenSubKey(BlockedKeyPath, writable: true);
            if (blockedKey is not null)
            {
                foreach (var clsid in FreezeProneShellExtensions.Keys)
                    blockedKey.DeleteValue(clsid, throwOnMissingValue: false);
            }

            Log.Instance.Trace($"ShellFreezeGuardService: reverted Explorer shell extension blocks.");
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"ShellFreezeGuardService.Revert failed.", ex);
        }
    }

    public async Task RestartExplorerAsync()
    {
        await Task.Run(() =>
        {
            foreach (var proc in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    proc.Kill();
                    proc.WaitForExit(3000);
                }
                catch
                {
                    // Ignore already-exited instances
                }
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            });
        }).ConfigureAwait(false);
    }
}
