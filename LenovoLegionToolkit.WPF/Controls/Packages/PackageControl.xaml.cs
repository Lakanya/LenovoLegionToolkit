using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.PackageDownloader;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Extensions;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Utils;
using Wpf.Ui.Common;
using Clipboard = System.Windows.Clipboard;

namespace LenovoLegionToolkit.WPF.Controls.Packages;

public partial class PackageControl : IProgress<float>
{
    private readonly IPackageDownloader _packageDownloader;
    private readonly Package _package;
    private readonly Func<string> _getDownloadPath;

    private CancellationTokenSource? _downloadPackageTokenSource;

    public bool IsDownloading { get; private set; }

    public Package Package => _package;

    public PackageControl(IPackageDownloader packageDownloader, Package package, Func<string> getDownloadPath)
    {
        _packageDownloader = packageDownloader;
        _package = package;
        _getDownloadPath = getDownloadPath;

        InitializeComponent();

        Unloaded += PackageControl_Unloaded;

        _titleTextBlock.Text = package.Title;

        var sourceLabel = string.IsNullOrWhiteSpace(package.SourceName) ? "Lenovo" : package.SourceName;
        _categorySourceTextBlock.Text = $"{package.Category}  ·  {sourceLabel}";

        // DriversCloud-style Installed -> Available version comparison
        if (!string.IsNullOrWhiteSpace(package.InstalledVersion))
        {
            _installedVersionLabel.Text = $"Installed: {package.InstalledVersion}";
            _installedVersionLabel.Visibility = Visibility.Visible;
            _versionArrowText.Visibility = Visibility.Visible;
            _availableVersionLabel.Text = $"Available: {package.Version}";
            if (package.IsUpdate)
                _availableVersionLabel.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
        }
        else
        {
            _availableVersionLabel.Text = $"Version: {package.Version}";
        }

        _descriptionTextBlock.Text = package.Description;
        _descriptionTextBlock.Visibility = string.IsNullOrWhiteSpace(package.Description) ? Visibility.Collapsed : Visibility.Visible;

        var dateStr = package.ReleaseDate.ToString(LocalizationHelper.ShortDateFormat);
        var sizeStr = string.IsNullOrWhiteSpace(package.FileSize) ? string.Empty : $"  ·  {package.FileSize}";
        _metaTextBlock.Text = $"{dateStr}{sizeStr}  ·  {package.FileName}";

        _readmeButton.Visibility = package.Readme is null ? Visibility.Collapsed : Visibility.Visible;

        // Status badge styling
        if (package.IsUpdate)
        {
            _statusBadge.Visibility = Visibility.Visible;
            _statusIcon.Symbol = package.IsBiosUpdate ? SymbolRegular.DeveloperBoard24 : SymbolRegular.ArrowSync24;
            _statusTextBlock.Text = package.IsBiosUpdate ? "BIOS Update Available" : "Update Available";
            _statusBadge.SetResourceReference(Border.BackgroundProperty, "SystemFillColorSuccessBackgroundBrush");
            _statusIcon.SetResourceReference(Control.ForegroundProperty, "SystemFillColorSuccessBrush");
            _statusTextBlock.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
        }
        else if (!package.IsUtilityOrTool && !string.IsNullOrWhiteSpace(package.InstalledVersion))
        {
            _statusBadge.Visibility = Visibility.Visible;
            _statusIcon.Symbol = SymbolRegular.CheckmarkCircle24;
            _statusTextBlock.Text = "Up to Date";
            _statusBadge.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
            _statusIcon.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
            _statusTextBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        }
        else if (package.IsUtilityOrTool)
        {
            _statusBadge.Visibility = Visibility.Visible;
            _statusIcon.Symbol = SymbolRegular.Toolbox24;
            _statusTextBlock.Text = "Utility / Tool";
            _statusBadge.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
            _statusIcon.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
            _statusTextBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        }
        else
        {
            _statusBadge.Visibility = Visibility.Collapsed;
        }

        // Reboot badge
        _rebootBadge.Visibility = package is { IsUpdate: true, Reboot: RebootType.Delayed or RebootType.Requested or RebootType.Forced or RebootType.ForcedPowerOff }
            ? Visibility.Visible
            : Visibility.Collapsed;
        _rebootTextBlock.Text = package.Reboot switch
        {
            RebootType.Delayed or RebootType.Requested => Resource.PackageControl_RebootRecommended,
            RebootType.Forced => Resource.PackageControl_RebootRequired,
            RebootType.ForcedPowerOff => Resource.PackageControl_ShutdownRequired,
            _ => string.Empty
        };

        // Primary action button clarity
        if (package.IsNvidiaUpstream)
        {
            _installButton.Content = "NVCleanstall";
            _installButton.Icon = SymbolRegular.Wrench24;
            _installButton.Appearance = package.IsUpdate ? ControlAppearance.Primary : ControlAppearance.Secondary;
            _installButton.ToolTip = "Open NVCleanstall for a clean, debloated NVIDIA driver install";
        }
        else if (package.IsBiosUpdate)
        {
            _installButton.Content = package.IsUpdate ? "Flash BIOS" : "Re-flash BIOS";
            _installButton.Icon = SymbolRegular.DeveloperBoard24;
            _installButton.Appearance = package.IsUpdate ? ControlAppearance.Primary : ControlAppearance.Secondary;
            _installButton.ToolTip = "Download and launch Lenovo's official BIOS updater";
        }
        else
        {
            _installButton.Content = package.IsUpdate ? "Update" : "Install";
            _installButton.Icon = SymbolRegular.Play24;
            _installButton.Appearance = package.IsUpdate ? ControlAppearance.Primary : ControlAppearance.Secondary;
            _installButton.ToolTip = "Download and install automatically";
        }
    }

    private void PackageControl_Unloaded(object sender, RoutedEventArgs e) => _downloadPackageTokenSource?.Cancel();

    public void Report(float value) => Dispatcher.Invoke(() =>
    {
        _downloadProgressRing.IsIndeterminate = !(value > 0);
        _downloadProgressRing.Progress = value * 100;
        _downloadProgressLabel.Content = $"{value * 100:0}{Resource.Percent}";
    });

    private async void CopyToClipboard_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (sender is not TextBlock tb)
            return;

        var str = tb.Text;

        try
        {
            Clipboard.SetText(str);
            await SnackbarHelper.ShowAsync(Resource.CopiedToClipboard_Title, string.Format(Resource.CopiedToClipboard_Message_WithParam, str));
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Couldn't copy to clipboard", ex);
        }
    }

    private void ReadmeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_package.Readme is null)
            return;

        new Uri(_package.Readme).Open();
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        IsDownloading = true;

        var result = false;

        try
        {
            _idleStackPanel.Visibility = Visibility.Collapsed;
            _downloadingStackPanel.Visibility = Visibility.Visible;

            if (_downloadPackageTokenSource is not null)
                await _downloadPackageTokenSource.CancelAsync();

            _downloadPackageTokenSource = new();

            var token = _downloadPackageTokenSource.Token;

            await _packageDownloader.DownloadPackageFileAsync(_package, _getDownloadPath(), this, token);

            result = true;
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            Log.Instance.Trace($"Not found 404.", ex);

            await SnackbarHelper.ShowAsync(Resource.PackageControl_Http404Error_Title, Resource.PackageControl_Http404Error_Message, SnackbarType.Error);
        }
        catch (HttpRequestException ex)
        {
            Log.Instance.Trace($"Error occurred when downloading package file.", ex);

            await SnackbarHelper.ShowAsync(Resource.PackageControl_HttpGeneralError_Title, Resource.PackageControl_HttpGeneralError_Message, SnackbarType.Error);
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Error occurred when downloading package file.", ex);

            await SnackbarHelper.ShowAsync(Resource.PackageControl_GeneralError_Title, ex.Message, SnackbarType.Error);
        }
        finally
        {
            _idleStackPanel.Visibility = Visibility.Visible;
            _downloadingStackPanel.Visibility = Visibility.Collapsed;
            _downloadProgressRing.Progress = 0;
            _downloadProgressLabel.Content = null;

            IsDownloading = false;
        }

        if (result)
            await SnackbarHelper.ShowAsync(Resource.PackageControl_DownloadComplete_Title, string.Format(Resource.PackageControl_DownloadComplete_Message, _package.FileName));
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_package.IsNvidiaUpstream)
        {
            _installButton.IsEnabled = false;
            try
            {
                var result = await NVCleanstallLauncher.LaunchOrInstallAsync();
                await SnackbarHelper.ShowAsync(
                    "NVCleanstall",
                    result.Message,
                    result.Success ? SnackbarType.Success : SnackbarType.Warning);
            }
            finally
            {
                _installButton.IsEnabled = true;
            }
            return;
        }

        if (_package.IsBiosUpdate)
        {
            var confirmed = await MessageBoxHelper.ShowAsync(
                this,
                "BIOS / Firmware Update",
                $"'{_package.Title}' ({_package.Version}) will launch Lenovo's official firmware updater.\n\nMake sure AC power is plugged in and do not power off during flashing. Continue?");
            if (!confirmed)
                return;
        }

        await DownloadAndInstallInternalAsync();
    }

    public async Task<PackageInstallResult> DownloadAndInstallInternalAsync()
    {
        IsDownloading = true;
        try
        {
            _idleStackPanel.Visibility = Visibility.Collapsed;
            _downloadingStackPanel.Visibility = Visibility.Visible;

            if (_downloadPackageTokenSource is not null)
                await _downloadPackageTokenSource.CancelAsync();

            _downloadPackageTokenSource = new();
            var token = _downloadPackageTokenSource.Token;

            var downloadDir = _getDownloadPath();
            var expectedPath = AbstractPackageDownloader.GetExpectedFilePath(_package, downloadDir);
            var installerPath = File.Exists(expectedPath)
                ? expectedPath
                : await _packageDownloader.DownloadPackageFileAsync(_package, downloadDir, this, token);

            _downloadProgressRing.IsIndeterminate = true;
            _downloadProgressLabel.Content = "Installing...";

            var installResult = await Task.Run(() => PackageInstaller.InstallAsync(_package, installerPath, token), token);

            if (installResult.Success)
            {
                var msg = installResult.RebootRequired
                    ? $"Installed {_package.Title} ({_package.Version}). A system restart is recommended."
                    : $"Successfully installed {_package.Title} ({_package.Version}).";
                await SnackbarHelper.ShowAsync("Installation complete", msg, installResult.RebootRequired ? SnackbarType.Info : SnackbarType.Success);
            }
            else
            {
                await SnackbarHelper.ShowAsync("Installation failed", installResult.ErrorMessage ?? $"Exit code {installResult.ExitCode}", SnackbarType.Error);
            }

            return installResult;
        }
        catch (OperationCanceledException)
        {
            return new(false, false, -1, "Canceled");
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Error occurred when installing package.", ex);
            await SnackbarHelper.ShowAsync(Resource.PackageControl_GeneralError_Title, ex.Message, SnackbarType.Error);
            return new(false, false, -1, ex.Message);
        }
        finally
        {
            _idleStackPanel.Visibility = Visibility.Visible;
            _downloadingStackPanel.Visibility = Visibility.Collapsed;
            _downloadProgressRing.Progress = 0;
            _downloadProgressLabel.Content = null;
            IsDownloading = false;
        }
    }

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e) => _downloadPackageTokenSource?.Cancel();
}
