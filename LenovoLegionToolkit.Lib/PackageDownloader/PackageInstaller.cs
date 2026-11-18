using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Utils;

namespace LenovoLegionToolkit.Lib.PackageDownloader;

public readonly record struct PackageInstallResult(bool Success, bool RebootRequired, int ExitCode, string? ErrorMessage = null);

public static class PackageInstaller
{
    public static async Task<PackageInstallResult> InstallAsync(Package package, string installerFilePath, CancellationToken token = default)
    {
        if (!File.Exists(installerFilePath))
            return new(false, false, -1, "Installer file not found.");

        // BIOS and firmware flashers must always run interactively so official AC/battery safety checks are shown.
        if (package.IsBiosUpdate || !installerFilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var interactivePsi = new ProcessStartInfo
            {
                FileName = installerFilePath,
                WorkingDirectory = Path.GetDirectoryName(installerFilePath) ?? string.Empty,
                UseShellExecute = true,
            };

            using var proc = Process.Start(interactivePsi);
            if (proc is null)
                return new(false, false, -1, "Failed to launch installer.");

            await proc.WaitForExitAsync(token).ConfigureAwait(false);
            var rebootReq = proc.ExitCode == 3010 || package.Reboot is RebootType.Forced or RebootType.Requested or RebootType.Delayed;
            return new(proc.ExitCode is 0 or 3010, rebootReq, proc.ExitCode);
        }

        var tempExtractDir = Path.Combine(Path.GetTempPath(), $"LLT_Pkg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempExtractDir);

        try
        {
            var args = BuildSilentArguments(package, tempExtractDir);
            Log.Instance.Trace($"Installing package '{package.Title}' silently: \"{installerFilePath}\" {args}");

            var psi = new ProcessStartInfo
            {
                FileName = installerFilePath,
                Arguments = args,
                WorkingDirectory = tempExtractDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return new(false, false, -1, "Failed to start silent installer.");

            await proc.WaitForExitAsync(token).ConfigureAwait(false);

            var exitCode = proc.ExitCode;
            var success = exitCode is 0 or 259 or 3010;
            var rebootNeeded = exitCode == 3010 || package.Reboot is RebootType.Forced or RebootType.Requested or RebootType.Delayed;

            if (!success)
            {
                Log.Instance.Trace($"Silent install exited with code {exitCode}; falling back to interactive installer.");
                var fallbackPsi = new ProcessStartInfo
                {
                    FileName = installerFilePath,
                    WorkingDirectory = Path.GetDirectoryName(installerFilePath) ?? string.Empty,
                    UseShellExecute = true,
                };
                using var fallbackProc = Process.Start(fallbackPsi);
                if (fallbackProc is not null)
                {
                    await fallbackProc.WaitForExitAsync(token).ConfigureAwait(false);
                    exitCode = fallbackProc.ExitCode;
                    success = exitCode is 0 or 3010;
                }
            }

            return new(success, rebootNeeded, exitCode, success ? null : $"Installer exited with code {exitCode}.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempExtractDir))
                    Directory.Delete(tempExtractDir, true);
            }
            catch { /* Ignore temp cleanup locks */ }
        }
    }

    private static string BuildSilentArguments(Package package, string tempExtractDir)
    {
        if (!string.IsNullOrWhiteSpace(package.InstallCommand))
        {
            var cmd = package.InstallCommand.Trim();
            var exeToken = package.FileName;
            var idx = cmd.IndexOf(exeToken, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var rawArgs = cmd[(idx + exeToken.Length)..].Trim().TrimStart('"').Trim();
                if (!string.IsNullOrWhiteSpace(rawArgs))
                    return rawArgs.Replace("%PACKAGEPATH%", tempExtractDir, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Standard Lenovo InnoSetup driver package silent flags
        return $"/VERYSILENT /NORESTART /SUPPRESSMSGBOXES /SP- /DIR=\"{Path.Combine(tempExtractDir, "TMP")}\"";
    }
}
