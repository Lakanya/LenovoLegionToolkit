using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.PackageDownloader;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Controls.Packages;
using LenovoLegionToolkit.WPF.Extensions;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Utils;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MenuItem = Wpf.Ui.Controls.MenuItem;

namespace LenovoLegionToolkit.WPF.Pages;

public partial class PackagesPage : IProgress<float>
{
    private enum FilterTab
    {
        Updates,
        Drivers,
        Tools
    }

    public static bool OpenUpdatesOnly { get; set; }

    private readonly PackageDownloaderSettings _packageDownloaderSettings = IoCContainer.Resolve<PackageDownloaderSettings>();
    private readonly PackageDownloaderFactory _packageDownloaderFactory = IoCContainer.Resolve<PackageDownloaderFactory>();
    private readonly DriverUpdateChecker _driverUpdateChecker = IoCContainer.Resolve<DriverUpdateChecker>();

    private IPackageDownloader? _packageDownloader;
    private CancellationTokenSource? _getPackagesTokenSource;
    private CancellationTokenSource? _filterDebounceCancellationTokenSource;

    private List<Package>? _packages;
    private MachineInformation _machineInfo;
    private FilterTab _activeTab = FilterTab.Updates;
    private bool _hasAutoScanned;

    public PackagesPage()
    {
        Initialized += PackagesPage_Initialized;
        Loaded += PackagesPage_Loaded;

        InitializeComponent();
    }

    private async void PackagesPage_Initialized(object? sender, EventArgs e)
    {
        _machineInfo = await Compatibility.GetMachineInformationAsync();
        _machineTypeTextBox.Text = _machineInfo.MachineType;
        _osComboBox.SetItems(Enum.GetValues<OS>(), OSExtensions.GetCurrent(), os => os.GetDisplayName());

        var downloadsFolder = KnownFolders.GetPath(KnownFolder.Downloads);
        _downloadToText.PlaceholderText = downloadsFolder;
        _downloadToText.Text = Directory.Exists(_packageDownloaderSettings.Store.DownloadPath)
            ? _packageDownloaderSettings.Store.DownloadPath
            : downloadsFolder;

        _downloadPackagesButton.IsEnabled = true;
        _cancelDownloadPackagesButton.IsEnabled = true;

        _sourceSmartRadio.Tag = PackageDownloaderFactory.Type.Smart;
        _sourcePrimaryRadio.Tag = PackageDownloaderFactory.Type.Vantage;
        _sourceSecondaryRadio.Tag = PackageDownloaderFactory.Type.PCSupport;

        if (_driverUpdateChecker.CachedPackages is { Count: > 0 } cached)
        {
            _hasAutoScanned = true;
            _packageDownloader = _packageDownloaderFactory.GetInstance(PackageDownloaderFactory.Type.Smart);
            _packages = cached.ToList();

            var hasUpdates = _packages.Any(p => p.IsUpdate && !p.IsUtilityOrTool && !_packageDownloaderSettings.Store.HiddenPackages.Contains(p.Id));
            _activeTab = OpenUpdatesOnly || hasUpdates ? FilterTab.Updates : FilterTab.Drivers;
            OpenUpdatesOnly = false;

            _loader.Visibility = Visibility.Visible;
            _loader.IsLoading = false;
            Reload();
        }
        else
        {
            UpdateHeroSummary();
        }
    }

    private async void PackagesPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (OpenUpdatesOnly && _driverUpdateChecker.CachedPackages is { Count: > 0 } cached)
        {
            OpenUpdatesOnly = false;
            _sourceSmartRadio.IsChecked = true;
            _packageDownloader = _packageDownloaderFactory.GetInstance(PackageDownloaderFactory.Type.Smart);
            _packages = cached.ToList();
            _activeTab = FilterTab.Updates;
            _loader.Visibility = Visibility.Visible;
            _loader.IsLoading = false;
            Reload();
            return;
        }

        // Automatically scan on first open if background cache isn't populated yet
        if (!_hasAutoScanned && _packages is null)
        {
            _hasAutoScanned = true;
            await ScanPackagesAsync();
        }
    }

    public void Report(float value) => Dispatcher.Invoke(() =>
    {
        _loader.IsIndeterminate = value < 0;
        _loader.Progress = value;
    });

    private void AdvancedOptionsToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _advancedOptionsCard.Visibility = _advancedOptionsCard.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void FilterTab_Click(object sender, RoutedEventArgs e)
    {
        if (!await ShouldInterruptDownloadsIfRunning())
            return;

        if (sender is not Wpf.Ui.Controls.Button btn || btn.Tag is not string tag)
            return;

        _activeTab = tag switch
        {
            "updates" => FilterTab.Updates,
            "tools" => FilterTab.Tools,
            _ => FilterTab.Drivers
        };

        _scrollViewer.ScrollToHome();
        Reload();
    }

    private void DownloadToText_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var location = _downloadToText.Text;

        if (!Directory.Exists(location))
            return;

        _packageDownloaderSettings.Store.DownloadPath = location;
        _packageDownloaderSettings.SynchronizeStore();
    }

    private void OpenDownloadToButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var location = GetDownloadLocation();
            if (!Directory.Exists(location))
                return;

            Process.Start("explorer", location);
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to open download location.", ex);
        }
    }

    private void DownloadToButton_Click(object sender, RoutedEventArgs e)
    {
        using var ofd = new FolderBrowserDialog();
        ofd.InitialDirectory = _downloadToText.Text;

        if (ofd.ShowDialog() != DialogResult.OK)
            return;

        var selectedPath = ofd.SelectedPath;
        _downloadToText.Text = selectedPath;
        _packageDownloaderSettings.Store.DownloadPath = selectedPath;
        _packageDownloaderSettings.SynchronizeStore();
    }

    private async void DownloadPackagesButton_Click(object sender, RoutedEventArgs e)
    {
        await ScanPackagesAsync();
    }

    private async Task ScanPackagesAsync()
    {
        if (!await ShouldInterruptDownloadsIfRunning())
            return;

        var errorOccurred = false;
        try
        {
            _downloadPackagesButton.Visibility = Visibility.Collapsed;
            _cancelDownloadPackagesButton.Visibility = Visibility.Visible;
            _installAllUpdatesButton.Visibility = Visibility.Collapsed;
            _heroStatusTitle.Text = "Scanning drivers & firmware...";
            _loader.Visibility = Visibility.Visible;
            _loader.IsLoading = true;
            _packages = null;

            _packagesStackPanel.Children.Clear();
            _scrollViewer.ScrollToHome();

            var machineType = _machineTypeTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(machineType) || machineType.Length != 4 ||
                !_osComboBox.TryGetSelectedItem(out OS os))
            {
                await SnackbarHelper.ShowAsync(Resource.PackagesPage_DownloadFailed_Title, Resource.PackagesPage_DownloadFailed_Message);
                return;
            }

            if (_getPackagesTokenSource is not null)
                await _getPackagesTokenSource.CancelAsync();

            _getPackagesTokenSource = new();
            var token = _getPackagesTokenSource.Token;

            var packageDownloaderType = new[] { _sourceSmartRadio, _sourcePrimaryRadio, _sourceSecondaryRadio }
                .Where(r => r.IsChecked == true)
                .Select(r => (PackageDownloaderFactory.Type)r.Tag)
                .First();

            _packageDownloader = _packageDownloaderFactory.GetInstance(packageDownloaderType);
            var packages = await _packageDownloader.GetPackagesAsync(machineType, os, this, token);

            _packages = packages;
            if (packageDownloaderType == PackageDownloaderFactory.Type.Smart)
                _driverUpdateChecker.UpdateCache(packages);

            var hasUpdates = packages.Any(p => p.IsUpdate && !p.IsUtilityOrTool && !_packageDownloaderSettings.Store.HiddenPackages.Contains(p.Id));
            _activeTab = hasUpdates ? FilterTab.Updates : FilterTab.Drivers;

            Reload();
        }
        catch (UpdateCatalogNotFoundException ex)
        {
            Log.Instance.Trace($"Update catalog not found.", ex);
            await SnackbarHelper.ShowAsync(Resource.PackagesPage_UpdateCatalogNotFound_Title, Resource.PackagesPage_UpdateCatalogNotFound_Message, SnackbarType.Info);
            errorOccurred = true;
        }
        catch (OperationCanceledException)
        {
            errorOccurred = true;
        }
        catch (HttpRequestException ex)
        {
            Log.Instance.Trace($"Error occurred when downloading packages.", ex);
            await SnackbarHelper.ShowAsync(Resource.PackagesPage_Error_Title, Resource.PackagesPage_Error_CheckInternet_Message, SnackbarType.Error);
            errorOccurred = true;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Error occurred when downloading packages.", ex);
            await SnackbarHelper.ShowAsync(Resource.PackagesPage_Error_Title, ex.Message, SnackbarType.Error);
            errorOccurred = true;
        }
        finally
        {
            _downloadPackagesButton.Visibility = Visibility.Visible;
            _cancelDownloadPackagesButton.Visibility = Visibility.Collapsed;
            _loader.IsLoading = false;
            _loader.Progress = 0;
            _loader.IsIndeterminate = true;

            if (errorOccurred)
            {
                _packagesStackPanel.Children.Clear();
                UpdateHeroSummary();
            }
        }
    }

    private void CancelDownloadPackagesButton_Click(object sender, RoutedEventArgs e) => _getPackagesTokenSource?.Cancel();

    private async void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!await ShouldInterruptDownloadsIfRunning())
            return;

        try
        {
            if (_packages is null)
                return;

            if (_filterDebounceCancellationTokenSource is not null)
                await _filterDebounceCancellationTokenSource.CancelAsync();

            _filterDebounceCancellationTokenSource = new();

            await Task.Delay(300, _filterDebounceCancellationTokenSource.Token);

            _scrollViewer.ScrollToHome();
            Reload();
        }
        catch (OperationCanceledException) { }
    }

    private async void SortingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!await ShouldInterruptDownloadsIfRunning())
            return;

        if (_packages is null)
            return;

        _scrollViewer.ScrollToHome();
        Reload();
    }

    private string GetDownloadLocation()
    {
        var location = _downloadToText.Text.Trim();

        if (!Directory.Exists(location))
        {
            var downloads = KnownFolders.GetPath(KnownFolder.Downloads);
            location = downloads;
            _downloadToText.Text = downloads;
            _packageDownloaderSettings.Store.DownloadPath = downloads;
            _packageDownloaderSettings.SynchronizeStore();
        }

        return location;
    }

    private ContextMenu? GetContextMenu(Package package, IEnumerable<Package> packages)
    {
        if (_packageDownloaderSettings.Store.HiddenPackages.Contains(package.Id))
            return null;

        var hideMenuItem = new MenuItem
        {
            SymbolIcon = SymbolRegular.EyeOff24,
            Header = Resource.Hide,
        };
        hideMenuItem.Click += (_, _) =>
        {
            _packageDownloaderSettings.Store.HiddenPackages.Add(package.Id);
            _packageDownloaderSettings.SynchronizeStore();
            Reload();
        };

        var hideAllMenuItem = new MenuItem
        {
            SymbolIcon = SymbolRegular.EyeOff24,
            Header = Resource.HideAll,
        };
        hideAllMenuItem.Click += (_, _) =>
        {
            foreach (var id in packages.Select(p => p.Id))
                _packageDownloaderSettings.Store.HiddenPackages.Add(id);
            _packageDownloaderSettings.SynchronizeStore();
            Reload();
        };

        var cm = new ContextMenu();
        cm.Items.Add(hideMenuItem);
        cm.Items.Add(hideAllMenuItem);
        return cm;
    }

    private async Task<bool> ShouldInterruptDownloadsIfRunning()
    {
        if (_packagesStackPanel?.Children is null)
            return true;

        if (_packagesStackPanel.Children.ToArray().OfType<PackageControl>().Where(pc => pc.IsDownloading).IsEmpty())
            return true;

        return await MessageBoxHelper.ShowAsync(this, Resource.PackagesPage_DownloadInProgress_Title, Resource.PackagesPage_DownloadInProgress_Message);
    }

    private void UpdateHeroSummary()
    {
        var biosLabel = !string.IsNullOrWhiteSpace(_machineInfo.BiosVersionRaw) ? $"BIOS {_machineInfo.BiosVersionRaw}" : "BIOS";
        var lastCheckLabel = _driverUpdateChecker.LastCheckUtc == DateTime.MinValue
            ? "Not scanned yet"
            : FormatLastChecked(_driverUpdateChecker.LastCheckUtc);

        _heroMachineSubtitle.Text = $"{_machineInfo.Model}  ·  {_machineInfo.MachineType}  ·  {OSExtensions.GetCurrent().GetDisplayName()}  ·  {biosLabel}  ·  Last scan: {lastCheckLabel}";

        if (_packages is null)
        {
            _heroStatusTitle.Text = "Ready to scan drivers & firmware";
            _heroIcon.Symbol = SymbolRegular.ArrowSync24;
            return;
        }

        var visible = _packages.Where(p => !_packageDownloaderSettings.Store.HiddenPackages.Contains(p.Id)).ToList();
        var updates = visible.Where(p => p.IsUpdate && !p.IsUtilityOrTool).ToList();
        var silentDriverUpdates = updates.Where(p => !p.IsBiosUpdate && !p.IsNvidiaUpstream).ToList();
        var driversCount = visible.Count(p => !p.IsUtilityOrTool);
        var toolsCount = visible.Count(p => p.IsUtilityOrTool);

        _tabUpdatesButton.Content = $"Updates ({updates.Count})";
        _tabDriversButton.Content = $"All Drivers ({driversCount})";
        _tabToolsButton.Content = $"Tools & Utilities ({toolsCount})";

        _tabUpdatesButton.Appearance = _activeTab == FilterTab.Updates ? ControlAppearance.Primary : ControlAppearance.Secondary;
        _tabDriversButton.Appearance = _activeTab == FilterTab.Drivers ? ControlAppearance.Primary : ControlAppearance.Secondary;
        _tabToolsButton.Appearance = _activeTab == FilterTab.Tools ? ControlAppearance.Primary : ControlAppearance.Secondary;

        if (updates.Count > 0)
        {
            var parts = new List<string>();
            var biosCount = updates.Count(p => p.IsBiosUpdate);
            var nvCount = updates.Count(p => p.IsNvidiaUpstream);
            if (biosCount > 0) parts.Add($"{biosCount} BIOS");
            if (nvCount > 0) parts.Add($"{nvCount} NVIDIA");
            if (silentDriverUpdates.Count > 0) parts.Add($"{silentDriverUpdates.Count} driver");

            _heroStatusTitle.Text = updates.Count == 1
                ? $"1 update available ({string.Join(", ", parts)})"
                : $"{updates.Count} updates available ({string.Join(", ", parts)})";

            _heroIcon.Symbol = SymbolRegular.ArrowDownload24;
            _heroIconBorder.SetResourceReference(Border.BackgroundProperty, "SystemFillColorCautionBackgroundBrush");
            _heroIcon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "SystemFillColorCautionBrush");
        }
        else
        {
            _heroStatusTitle.Text = "All drivers & firmware are up to date";
            _heroIcon.Symbol = SymbolRegular.CheckmarkCircle24;
            _heroIconBorder.SetResourceReference(Border.BackgroundProperty, "SystemFillColorSuccessBackgroundBrush");
            _heroIcon.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "SystemFillColorSuccessBrush");
        }

        _installAllUpdatesButton.Visibility = silentDriverUpdates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _installAllUpdatesButton.Content = silentDriverUpdates.Count > 1
            ? $"Update All ({silentDriverUpdates.Count})"
            : "Update 1 Driver";
    }

    private static string FormatLastChecked(DateTime utcTime)
    {
        var diff = DateTime.UtcNow - utcTime;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        return utcTime.ToLocalTime().ToString("g");
    }

    private void Reload()
    {
        if (_packageDownloader is null)
            return;

        _packagesStackPanel.Children.Clear();
        UpdateHeroSummary();

        if (_packages is null || _packages.Count == 0)
            return;

        var packages = SortAndFilter(_packages);

        foreach (var package in packages)
        {
            var control = new PackageControl(_packageDownloader, package, GetDownloadLocation)
            {
                ContextMenu = GetContextMenu(package, packages)
            };
            _packagesStackPanel.Children.Add(control);
        }

        if (packages.IsEmpty())
        {
            var emptyPanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new(0, 36, 0, 36)
            };

            if (_activeTab == FilterTab.Updates && string.IsNullOrWhiteSpace(_filterTextBox.Text))
            {
                emptyPanel.Children.Add(new SymbolIcon
                {
                    Symbol = SymbolRegular.CheckmarkCircle24,
                    FontSize = 36,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = (Brush)FindResource("SystemFillColorSuccessBrush")
                });
                emptyPanel.Children.Add(new TextBlock
                {
                    Text = "Everything is up to date!",
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new(0, 10, 0, 4)
                });
                emptyPanel.Children.Add(new TextBlock
                {
                    Text = "Your installed drivers and BIOS match the latest official releases.",
                    Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new(0, 0, 0, 14)
                });

                var viewAllBtn = new Wpf.Ui.Controls.Button
                {
                    Content = "View All Drivers",
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                viewAllBtn.Click += (_, _) =>
                {
                    _activeTab = FilterTab.Drivers;
                    Reload();
                };
                emptyPanel.Children.Add(viewAllBtn);
            }
            else
            {
                emptyPanel.Children.Add(new TextBlock
                {
                    Text = Resource.PackagesPage_NoMatchingDownloads,
                    Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Focusable = true
                });
            }

            _packagesStackPanel.Children.Add(emptyPanel);
        }

        if (_packageDownloaderSettings.Store.HiddenPackages.Count != 0)
        {
            var clearHidden = new Hyperlink
            {
                Icon = SymbolRegular.Eye24,
                Content = $"Show {_packageDownloaderSettings.Store.HiddenPackages.Count} hidden package(s)",
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            clearHidden.Click += (_, _) =>
            {
                _packageDownloaderSettings.Store.HiddenPackages.Clear();
                _packageDownloaderSettings.SynchronizeStore();
                Reload();
            };
            _packagesStackPanel.Children.Add(clearHidden);
        }
    }

    private async void InstallAllUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_packages is null || _packageDownloader is null)
            return;

        if (_activeTab != FilterTab.Updates)
        {
            _activeTab = FilterTab.Updates;
            Reload();
        }

        var controls = _packagesStackPanel.Children
            .OfType<PackageControl>()
            .Where(c => c.Package.IsUpdate && !c.Package.IsBiosUpdate && !c.Package.IsNvidiaUpstream && !c.Package.IsUtilityOrTool)
            .ToArray();

        if (controls.Length == 0)
            return;

        _installAllUpdatesButton.IsEnabled = false;
        try
        {
            var anyReboot = false;
            var installedCount = 0;

            foreach (var control in controls)
            {
                var res = await control.DownloadAndInstallInternalAsync();
                if (res.Success)
                {
                    installedCount++;
                    anyReboot |= res.RebootRequired;
                }
            }

            if (installedCount > 0)
            {
                var msg = anyReboot
                    ? $"Installed {installedCount} driver update(s). Please restart Windows to finish applying changes."
                    : $"Installed {installedCount} driver update(s).";
                await SnackbarHelper.ShowAsync("Batch update complete", msg, anyReboot ? SnackbarType.Info : SnackbarType.Success);
            }
        }
        finally
        {
            _installAllUpdatesButton.IsEnabled = true;
        }
    }

    private List<Package> SortAndFilter(List<Package> packages)
    {
        IEnumerable<Package> result = packages
            .Where(p => !_packageDownloaderSettings.Store.HiddenPackages.Contains(p.Id));

        result = _activeTab switch
        {
            FilterTab.Updates => result.Where(p => p.IsUpdate && !p.IsUtilityOrTool),
            FilterTab.Tools => result.Where(p => p.IsUtilityOrTool),
            _ => result.Where(p => !p.IsUtilityOrTool)
        };

        if (!string.IsNullOrWhiteSpace(_filterTextBox.Text))
            result = result.Where(p => p.Index.Contains(_filterTextBox.Text, StringComparison.InvariantCultureIgnoreCase));

        result = _sortingComboBox.SelectedIndex switch
        {
            1 => result.OrderByDescending(p => p.ReleaseDate),
            2 => result.OrderBy(p => p.Title),
            3 => result.OrderBy(p => p.Category).ThenByDescending(p => p.IsUpdate),
            _ => result
                .OrderByDescending(p => p.IsUpdate)
                .ThenByDescending(p => p.IsBiosUpdate)
                .ThenByDescending(p => p.IsNvidiaUpstream)
                .ThenByDescending(p => p.ReleaseDate)
        };

        return result.ToList();
    }
}
