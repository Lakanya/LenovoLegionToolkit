using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.PackageDownloader;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Extensions;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Utils;

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

        _dateTextBlock.Text = package.ReleaseDate.ToString(LocalizationHelper.ShortDateFormat);
        _titleTextBlock.Text = package.Title;
        _descriptionTextBlock.Text = package.Description;
        _descriptionTextBlock.Visibility = string.IsNullOrWhiteSpace(package.Description) ? Visibility.Collapsed : Visibility.Visible;
        _categoryTextBlock.Text = package.Category;
        _detailTextBlock.Text = $"{Resource.PackageControl_Version} {package.Version}  |  {package.FileSize}  |  {package.FileName}";

        _readmeButton.Visibility = package.Readme is null ? Visibility.Collapsed : Visibility.Visible;
        _updateRebootStackPanel.Visibility = _isUpdateStackPanel.Visibility = package.IsUpdate ? Visibility.Visible : Visibility.Collapsed;

        _rebootStackPanel.Visibility = package is { IsUpdate: true, Reboot: RebootType.Delayed or RebootType.Requested or RebootType.Forced or RebootType.ForcedPowerOff }
            ? Visibility.Visible
            : Visibility.Collapsed;
        _rebootTextBlock.Text = package.Reboot switch
        {
            RebootType.Delayed or RebootType.Requested => Resource.PackageControl_RebootRecommended,
            RebootType.Forced => Resource.PackageControl_RebootRequired,
            RebootType.ForcedPowerOff => Resource.PackageControl_ShutdownRequired,
            _ => string.Empty
        };

        var showWarning = package.ReleaseDate < DateTime.UtcNow.AddYears(-1);
        _warningTextBlock.Visibility = showWarning ? Visibility.Visible : Visibility.Collapsed;

        if (package.IsNvidiaUpstream)
        {
            _installButton.ToolTip = "Open in NVCleanstall (debloated install)";
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
