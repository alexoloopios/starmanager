using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StarManager.App.Models;
using StarManager.App.Services;

namespace StarManager.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly StarDiscoveryService _discoveryService = new();
    private readonly ProviderProcessService _providerProcessService = new();
    private readonly CoagulatorProcessService _coagulatorProcessService = new();
    private readonly ProviderIniEditorService _providerIniEditorService = new();
    private readonly SettingsService _settingsService = new();

    private string? _selectedStarRoot;
    private StarScanResult? _scanResult;
    private AppSettings _settings = new();
    private bool _isDarkThemeActive;
    private string _providerSearchQuery = string.Empty;
    private bool _showOnlyNeedingSetup;
    private bool _isUpdatingRecentStarPathSelection;
    private bool _isInitializingUi = true;
    private ProviderItem? _selectedProvider;
    private string? _selectedProviderIniPath;
    private bool _isLoadingProviderIniContent;
    private bool _hasUnsavedProviderIniChanges;
    private bool _isRestoringProviderSelection;
    private bool _isScanning;
    private int _scanProgressGeneration;

    private const int MaxRecentStarPaths = 10;
    private const int MaxActivityLogLines = 300;

    public ObservableCollection<ProviderItem> Providers { get; } = [];
    public ObservableCollection<string> RecentStarPaths { get; } = [];
    public ICollectionView ProvidersView { get; }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        ProvidersView = CollectionViewSource.GetDefaultView(Providers);
        ProvidersView.Filter = ProviderMatchesSearchQuery;
        SourceInitialized += OnSourceInitialized;
        LoadSettings();
        _isInitializingUi = false;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyWindowChromeTheme(_isDarkThemeActive);
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        // Delay focus assignment until layout completes to avoid focus being stolen by template initialization.
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (BrowseButton.IsVisible && BrowseButton.IsEnabled)
            {
                _ = BrowseButton.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        if (ConfirmPendingProviderIniChanges("closing the app"))
        {
            return;
        }

        e.Cancel = true;
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        if (e.Key is Key.D1 or Key.NumPad1)
        {
            MainSectionsTabControl.SelectedItem = ProvidersTabItem;
            e.Handled = true;
            return;
        }

        if (e.Key is Key.D2 or Key.NumPad2)
        {
            MainSectionsTabControl.SelectedItem = CoagulatorsTabItem;
            e.Handled = true;
        }
    }

    private async void BrowseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isScanning)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select your STAR root folder",
        };

        var result = dialog.ShowDialog(this);
        if (result != true)
        {
            return;
        }

        _selectedStarRoot = dialog.FolderName;
        StarPathTextBox.Text = _selectedStarRoot;
        AddRecentStarPath(_selectedStarRoot);
        LogStatus("STAR folder selected. Running initial scan.");
        await ScanSelectedStarRootAsync();
    }

    private async void ScanButton_OnClick(object sender, RoutedEventArgs e)
    {
        await ScanSelectedStarRootAsync();
    }

    private async Task ScanSelectedStarRootAsync()
    {
        if (_isScanning)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedStarRoot))
        {
            LogStatus("Select a STAR folder first.");
            return;
        }

        var currentScanGeneration = ++_scanProgressGeneration;
        _isScanning = true;
        SetScanningUiState(isScanning: true);
        ShowScanProgress(10, "Scanning STAR setup...");

        try
        {
            var scanRoot = _selectedStarRoot;
            var scanResult = await Task.Run(() => _discoveryService.Scan(scanRoot));
            _scanResult = scanResult;

            ShowScanProgress(55, "Applying scan results...");
            Providers.Clear();

            foreach (var provider in _scanResult.Providers)
            {
                provider.StatusText = _providerProcessService.IsRunning(provider) ? "Running" : "Stopped";
                provider.RequiresConfigureFirst = DetermineRequiresConfigureFirst(provider);
                Providers.Add(provider);
            }

            ShowScanProgress(80, "Refreshing provider list...");
            ProvidersView.Refresh();
            ScanDiagnosticsTextBox.Text = string.Join(Environment.NewLine, _scanResult.Diagnostics);

            ProvidersDataGrid.SelectedIndex = Providers.Count > 0 ? 0 : -1;
            UpdateSelectedProviderState();

            ShowScanProgress(100, "Scan complete.");

            LogStatus(
                $"Scan complete: {_scanResult.Providers.Count} provider(s) found. " +
                $"STAR app: {(string.IsNullOrWhiteSpace(_scanResult.StarAppEntryPath) ? "missing" : "found")}. " +
                $"Coagulator: {(string.IsNullOrWhiteSpace(_scanResult.CoagulatorEntryPath) ? "missing" : "found")}."
            );
        }
        catch (Exception ex)
        {
            ShowScanProgress(100, "Scan failed.");
            ScanDiagnosticsTextBox.Text = ex.ToString();
            LogStatus($"Scan failed: {ex.Message}", isError: true);
        }
        finally
        {
            _isScanning = false;
            SetScanningUiState(isScanning: false);
            _ = HideScanProgressAfterDelayAsync(currentScanGeneration);
        }
    }

    private void ConfigureProviderButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetProviderFromButton(sender, out var provider))
        {
            return;
        }

        ExecuteConfigureProvider(provider);
    }

    private void StartProviderButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetProviderFromButton(sender, out var provider))
        {
            return;
        }

        ExecuteStartProvider(provider);
    }

    private async void StopProviderButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetProviderFromButton(sender, out var provider))
        {
            return;
        }

        await ExecuteStopProviderAsync(provider);
    }

    private void ProvidersDataGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRestoringProviderSelection)
        {
            UpdateSelectedProviderState();
            return;
        }

        var previousProvider = e.RemovedItems.OfType<ProviderItem>().FirstOrDefault();
        var nextProvider = e.AddedItems.OfType<ProviderItem>().FirstOrDefault();
        var changedProvider = !string.Equals(previousProvider?.EntryPath, nextProvider?.EntryPath, StringComparison.OrdinalIgnoreCase);

        if (changedProvider && !ConfirmPendingProviderIniChanges("switching providers"))
        {
            _isRestoringProviderSelection = true;
            ProvidersDataGrid.SelectedItem = previousProvider;
            _isRestoringProviderSelection = false;
            return;
        }

        UpdateSelectedProviderState();
    }

    private void ConfigureSelectedProviderButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProvider is null)
        {
            return;
        }

        ExecuteConfigureProvider(_selectedProvider);
    }

    private void StartSelectedProviderButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProvider is null)
        {
            return;
        }

        ExecuteStartProvider(_selectedProvider);
    }

    private async void StopSelectedProviderButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProvider is null)
        {
            return;
        }

        await ExecuteStopProviderAsync(_selectedProvider);
    }

    private void UpdateSelectedProviderState()
    {
        var previousProviderEntryPath = _selectedProvider?.EntryPath;
        _selectedProvider = ProvidersDataGrid.SelectedItem as ProviderItem;
        var selectedProviderChanged = !string.Equals(previousProviderEntryPath, _selectedProvider?.EntryPath, StringComparison.OrdinalIgnoreCase);

        if (selectedProviderChanged)
        {
            ClearLoadedProviderIniEditor(clearText: true);
        }

        var hasSelection = _selectedProvider is not null;
        ConfigureSelectedProviderButton.IsEnabled = hasSelection;
        StartSelectedProviderButton.IsEnabled = hasSelection;
        StopSelectedProviderButton.IsEnabled = hasSelection;
        LoadProviderIniButton.IsEnabled = hasSelection;

        if (string.IsNullOrWhiteSpace(_selectedProviderIniPath))
        {
            SelectedProviderIniPathTextBlock.Text = hasSelection
                ? "INI file: none loaded"
                : "INI file: select a provider to load its settings";
        }

        SelectedProviderTextBlock.Text = hasSelection
            ? $"Selected provider: {_selectedProvider!.Name}"
            : "Selected provider: none";
    }

    private void LoadProviderIniButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProvider is null)
        {
            LogStatus("Select a provider before loading an INI file.", isError: true);
            return;
        }

        if (!ConfirmPendingProviderIniChanges("loading a different INI file"))
        {
            return;
        }

        try
        {
            var iniFilePath = _providerIniEditorService.FindLikelyIniFile(_selectedProvider);
            if (string.IsNullOrWhiteSpace(iniFilePath))
            {
                ClearLoadedProviderIniEditor(clearText: true);
                LogStatus($"No INI file found for provider '{_selectedProvider.Name}'.", isError: true);
                return;
            }

            LoadIniIntoEditor(iniFilePath);
            LogStatus($"Loaded INI for provider '{_selectedProvider.Name}': {iniFilePath}");
        }
        catch (Exception ex)
        {
            LogStatus($"Could not load provider INI: {ex.Message}", isError: true);
        }
    }

    private void ReloadProviderIniButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedProviderIniPath))
        {
            LogStatus("No INI file is currently loaded.", isError: true);
            return;
        }

        if (!ConfirmPendingProviderIniChanges("reloading this INI file"))
        {
            return;
        }

        try
        {
            LoadIniIntoEditor(_selectedProviderIniPath);
            LogStatus($"Reloaded INI from '{_selectedProviderIniPath}'.");
        }
        catch (Exception ex)
        {
            LogStatus($"Could not reload INI: {ex.Message}", isError: true);
        }
    }

    private void SaveProviderIniButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedProviderIniPath))
        {
            LogStatus("No INI file is currently loaded.", isError: true);
            return;
        }

        try
        {
            var backupFilePath = _providerIniEditorService.SaveIniFileSafely(_selectedProviderIniPath, ProviderIniEditorTextBox.Text);
            _hasUnsavedProviderIniChanges = false;
            UpdateProviderIniPathLabel();
            RefreshProviderConfigurationState(_selectedProvider);

            if (string.IsNullOrWhiteSpace(backupFilePath))
            {
                LogStatus($"Saved INI: {_selectedProviderIniPath}");
                return;
            }

            LogStatus($"Saved INI: {_selectedProviderIniPath} (backup: {backupFilePath})");
        }
        catch (Exception ex)
        {
            LogStatus($"Could not save INI: {ex.Message}", isError: true);
        }
    }

    private void LoadIniIntoEditor(string iniFilePath)
    {
        _isLoadingProviderIniContent = true;
        try
        {
            ProviderIniEditorTextBox.Text = _providerIniEditorService.ReadIniFile(iniFilePath);
        }
        finally
        {
            _isLoadingProviderIniContent = false;
        }

        _selectedProviderIniPath = iniFilePath;
        _hasUnsavedProviderIniChanges = false;
        ProviderIniEditorTextBox.IsEnabled = true;
        ReloadProviderIniButton.IsEnabled = true;
        SaveProviderIniButton.IsEnabled = true;
        UpdateProviderIniPathLabel();
    }

    private void ClearLoadedProviderIniEditor(bool clearText)
    {
        _selectedProviderIniPath = null;
        _hasUnsavedProviderIniChanges = false;

        if (clearText)
        {
            _isLoadingProviderIniContent = true;
            ProviderIniEditorTextBox.Clear();
            _isLoadingProviderIniContent = false;
        }

        ProviderIniEditorTextBox.IsEnabled = false;
        ReloadProviderIniButton.IsEnabled = false;
        SaveProviderIniButton.IsEnabled = false;
    }

    private void ExecuteConfigureProvider(ProviderItem provider)
    {
        try
        {
            _providerProcessService.LaunchConfigure(provider);
            RefreshProviderConfigurationState(provider);
            LogStatus($"Opened configure UI for provider '{provider.Name}'.");
        }
        catch (Exception ex)
        {
            LogStatus($"Could not configure '{provider.Name}': {ex.Message}", isError: true);
        }
    }

    private void ExecuteStartProvider(ProviderItem provider)
    {
        try
        {
            _providerProcessService.StartProvider(provider);
            RefreshProviderConfigurationState(provider);
            provider.StatusText = "Running";
            LogStatus($"Started provider '{provider.Name}'.");
        }
        catch (Exception ex)
        {
            LogStatus($"Could not start '{provider.Name}': {ex.Message}", isError: true);
        }
    }

    private async Task ExecuteStopProviderAsync(ProviderItem provider)
    {

        try
        {
            await _providerProcessService.StopProviderAsync(provider);
            provider.StatusText = "Stopped";
            LogStatus($"Stopped provider '{provider.Name}'.");
        }
        catch (Exception ex)
        {
            LogStatus($"Could not stop '{provider.Name}': {ex.Message}", isError: true);
        }
    }

    private void LaunchStarAppButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_scanResult is null || string.IsNullOrWhiteSpace(_scanResult.StarAppEntryPath))
        {
            LogStatus("STAR app entrypoint was not detected in this setup.", isError: true);
            return;
        }

        try
        {
            var startInfo = BuildProcessStartInfo(_scanResult.StarAppEntryPath);
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not launch STAR app process.");

            LogStatus("STAR app launched.");
        }
        catch (Exception ex)
        {
            LogStatus($"Could not launch STAR app: {ex.Message}", isError: true);
        }
    }

    private void OpenCoagulatorWebsiteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_scanResult is null || string.IsNullOrWhiteSpace(_scanResult.CoagulatorWebsiteUrl))
        {
            LogStatus("No coagulator website URL was detected from configuration.", isError: true);
            return;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = _scanResult.CoagulatorWebsiteUrl,
                UseShellExecute = true,
            });

            LogStatus("Opened coagulator website.");
        }
        catch (Exception ex)
        {
            LogStatus($"Could not open website: {ex.Message}", isError: true);
        }
    }

    private void ThemeComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi)
        {
            return;
        }

        if (ThemeComboBox.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }

        var themeName = selected.Content?.ToString() ?? "System";
        ApplyTheme(themeName);
        _settings.ThemeName = themeName;
        _settingsService.Save(_settings);
    }

    private void StartCoagulatorButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_scanResult is null || string.IsNullOrWhiteSpace(_scanResult.CoagulatorEntryPath))
        {
            LogStatus("Coagulator entrypoint was not detected in this setup.", isError: true);
            return;
        }

        try
        {
            _coagulatorProcessService.Start(_scanResult.CoagulatorEntryPath);
            UpdateCoagulatorStatusText();
            LogStatus("Coagulator started.");
        }
        catch (Exception ex)
        {
            LogStatus($"Could not start coagulator: {ex.Message}", isError: true);
        }
    }

    private async void StopCoagulatorButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _coagulatorProcessService.StopAsync();
            UpdateCoagulatorStatusText();
            LogStatus("Coagulator stopped.");
        }
        catch (Exception ex)
        {
            LogStatus($"Could not stop coagulator: {ex.Message}", isError: true);
        }
    }

    private bool TryGetProviderFromButton(object sender, out ProviderItem provider)
    {
        provider = null!;

        if (sender is not Button { Tag: ProviderItem taggedProvider })
        {
            return false;
        }

        provider = taggedProvider;
        return true;
    }

    private void ProviderSearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _providerSearchQuery = ProviderSearchTextBox.Text.Trim();
        ProvidersView.Refresh();
    }

    private void ShowNeedingSetupOnlyCheckBox_OnCheckedChanged(object sender, RoutedEventArgs e)
    {
        _showOnlyNeedingSetup = ShowNeedingSetupOnlyCheckBox.IsChecked == true;
        ProvidersView.Refresh();
    }

    private void ClearProviderSearchButton_OnClick(object sender, RoutedEventArgs e)
    {
        ProviderSearchTextBox.Clear();
        _ = ProviderSearchTextBox.Focus();
    }

    private async void RecentStarPathsComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingRecentStarPathSelection)
        {
            return;
        }

        if (_isScanning)
        {
            return;
        }

        if (RecentStarPathsComboBox.SelectedItem is not string selectedPath)
        {
            return;
        }

        if (!Directory.Exists(selectedPath))
        {
            LogStatus("Selected recent STAR path no longer exists.", isError: true);
            return;
        }

        _selectedStarRoot = selectedPath;
        StarPathTextBox.Text = selectedPath;
        AddRecentStarPath(selectedPath);
        LogStatus("Recent STAR setup selected. Running initial scan.");
        await ScanSelectedStarRootAsync();
    }

    private bool ProviderMatchesSearchQuery(object item)
    {
        if (item is not ProviderItem provider)
        {
            return false;
        }

        if (_showOnlyNeedingSetup && !provider.RequiresConfigureFirst)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_providerSearchQuery))
        {
            return true;
        }

        return provider.Name.Contains(_providerSearchQuery, StringComparison.OrdinalIgnoreCase);
    }

    private bool DetermineRequiresConfigureFirst(ProviderItem provider)
    {
        try
        {
            return !_providerIniEditorService.HasConfiguredValues(provider);
        }
        catch
        {
            return true;
        }
    }

    private void RefreshProviderConfigurationState(ProviderItem? provider)
    {
        if (provider is null)
        {
            return;
        }

        provider.RequiresConfigureFirst = DetermineRequiresConfigureFirst(provider);
        ProvidersView.Refresh();
    }

    private static ProcessStartInfo BuildProcessStartInfo(string entryPath)
    {
        var directory = Path.GetDirectoryName(entryPath)
            ?? throw new InvalidOperationException("The entrypoint path has no parent directory.");

        if (Path.GetExtension(entryPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessStartInfo
            {
                FileName = entryPath,
                WorkingDirectory = directory,
                UseShellExecute = true,
            };
        }

        return new ProcessStartInfo
        {
            FileName = "pythonw",
            WorkingDirectory = directory,
            UseShellExecute = true,
            Arguments = $"\"{entryPath}\"",
        };
    }

    private void ApplyTheme(string themeName)
    {
        var useDarkTheme = themeName.Equals("Dark", StringComparison.OrdinalIgnoreCase)
            || (themeName.Equals("System", StringComparison.OrdinalIgnoreCase)
                && IsSystemInDarkMode());

        _isDarkThemeActive = useDarkTheme;
        ApplyThemeResources(useDarkTheme);
        ApplyWindowChromeTheme(useDarkTheme);
    }

    private static void ApplyThemeResources(bool useDarkTheme)
    {
        if (useDarkTheme)
        {
            SetThemeBrush("AppWindowBackgroundBrush", Color.FromRgb(22, 25, 31));
            SetThemeBrush("AppSurfaceBrush", Color.FromRgb(30, 35, 43));
            SetThemeBrush("AppControlBackgroundBrush", Color.FromRgb(37, 43, 53));
            SetThemeBrush("AppControlForegroundBrush", Color.FromRgb(243, 244, 246));
            SetThemeBrush("AppSecondaryForegroundBrush", Color.FromRgb(199, 206, 217));
            SetThemeBrush("AppBorderBrush", Color.FromRgb(150, 163, 184));
            SetThemeBrush("AppAccentBrush", Color.FromRgb(139, 184, 255));
            SetThemeBrush("AppDataGridAltRowBrush", Color.FromRgb(35, 42, 52));
            SetThemeBrush("AppSelectionBrush", Color.FromRgb(51, 90, 148));
            SetThemeBrush("AppControlHoverBrush", Color.FromRgb(45, 53, 66));
            SetThemeBrush("AppControlPressedBrush", Color.FromRgb(58, 71, 90));
            SetThemeBrush("AppDisabledBackgroundBrush", Color.FromRgb(47, 54, 66));
            SetThemeBrush("AppDisabledForegroundBrush", Color.FromRgb(164, 176, 194));
            SetThemeBrush("AppFocusBrush", Color.FromRgb(139, 184, 255));
            SetThemeBrush("AppTabActiveBackgroundBrush", Color.FromRgb(44, 67, 103));
            SetThemeBrush("AppBadgeRunningBackgroundBrush", Color.FromRgb(28, 58, 42));
            SetThemeBrush("AppBadgeRunningForegroundBrush", Color.FromRgb(185, 242, 206));
            SetThemeBrush("AppBadgeRunningBorderBrush", Color.FromRgb(63, 138, 99));
            SetThemeBrush("AppBadgeStoppedBackgroundBrush", Color.FromRgb(43, 50, 64));
            SetThemeBrush("AppBadgeStoppedForegroundBrush", Color.FromRgb(213, 217, 225));
            SetThemeBrush("AppBadgeStoppedBorderBrush", Color.FromRgb(139, 150, 170));

            SetThemeBrush(SystemColors.WindowBrushKey, Color.FromRgb(30, 35, 43));
            SetThemeBrush(SystemColors.WindowTextBrushKey, Color.FromRgb(243, 244, 246));
            SetThemeBrush(SystemColors.ControlBrushKey, Color.FromRgb(37, 43, 53));
            SetThemeBrush(SystemColors.ControlTextBrushKey, Color.FromRgb(243, 244, 246));
            SetThemeBrush(SystemColors.GrayTextBrushKey, Color.FromRgb(164, 176, 194));
            SetThemeBrush(SystemColors.HighlightBrushKey, Color.FromRgb(51, 90, 148));
            SetThemeBrush(SystemColors.HighlightTextBrushKey, Color.FromRgb(243, 244, 246));
            SetThemeBrush(SystemColors.InactiveSelectionHighlightBrushKey, Color.FromRgb(45, 53, 66));
            SetThemeBrush(SystemColors.InactiveSelectionHighlightTextBrushKey, Color.FromRgb(243, 244, 246));
            SetThemeBrush(SystemColors.MenuBrushKey, Color.FromRgb(37, 43, 53));
            SetThemeBrush(SystemColors.MenuTextBrushKey, Color.FromRgb(243, 244, 246));
            return;
        }

        SetThemeBrush("AppWindowBackgroundBrush", Color.FromRgb(244, 246, 248));
        SetThemeBrush("AppSurfaceBrush", Color.FromRgb(255, 255, 255));
        SetThemeBrush("AppControlBackgroundBrush", Color.FromRgb(255, 255, 255));
        SetThemeBrush("AppControlForegroundBrush", Color.FromRgb(17, 24, 39));
        SetThemeBrush("AppSecondaryForegroundBrush", Color.FromRgb(55, 65, 81));
        SetThemeBrush("AppBorderBrush", Color.FromRgb(139, 147, 161));
        SetThemeBrush("AppAccentBrush", Color.FromRgb(29, 78, 216));
        SetThemeBrush("AppDataGridAltRowBrush", Color.FromRgb(238, 242, 247));
        SetThemeBrush("AppSelectionBrush", Color.FromRgb(220, 235, 255));
        SetThemeBrush("AppControlHoverBrush", Color.FromRgb(243, 247, 255));
        SetThemeBrush("AppControlPressedBrush", Color.FromRgb(215, 231, 255));
        SetThemeBrush("AppDisabledBackgroundBrush", Color.FromRgb(241, 243, 246));
        SetThemeBrush("AppDisabledForegroundBrush", Color.FromRgb(99, 107, 120));
        SetThemeBrush("AppFocusBrush", Color.FromRgb(29, 78, 216));
        SetThemeBrush("AppTabActiveBackgroundBrush", Color.FromRgb(231, 240, 255));
        SetThemeBrush("AppBadgeRunningBackgroundBrush", Color.FromRgb(232, 247, 238));
        SetThemeBrush("AppBadgeRunningForegroundBrush", Color.FromRgb(15, 81, 50));
        SetThemeBrush("AppBadgeRunningBorderBrush", Color.FromRgb(121, 198, 155));
        SetThemeBrush("AppBadgeStoppedBackgroundBrush", Color.FromRgb(241, 243, 246));
        SetThemeBrush("AppBadgeStoppedForegroundBrush", Color.FromRgb(55, 65, 81));
        SetThemeBrush("AppBadgeStoppedBorderBrush", Color.FromRgb(156, 163, 175));

        SetThemeBrush(SystemColors.WindowBrushKey, Color.FromRgb(255, 255, 255));
        SetThemeBrush(SystemColors.WindowTextBrushKey, Color.FromRgb(17, 24, 39));
        SetThemeBrush(SystemColors.ControlBrushKey, Color.FromRgb(255, 255, 255));
        SetThemeBrush(SystemColors.ControlTextBrushKey, Color.FromRgb(17, 24, 39));
        SetThemeBrush(SystemColors.GrayTextBrushKey, Color.FromRgb(99, 107, 120));
        SetThemeBrush(SystemColors.HighlightBrushKey, Color.FromRgb(220, 235, 255));
        SetThemeBrush(SystemColors.HighlightTextBrushKey, Color.FromRgb(17, 24, 39));
        SetThemeBrush(SystemColors.InactiveSelectionHighlightBrushKey, Color.FromRgb(238, 242, 247));
        SetThemeBrush(SystemColors.InactiveSelectionHighlightTextBrushKey, Color.FromRgb(17, 24, 39));
        SetThemeBrush(SystemColors.MenuBrushKey, Color.FromRgb(255, 255, 255));
        SetThemeBrush(SystemColors.MenuTextBrushKey, Color.FromRgb(17, 24, 39));
    }

    private static void SetThemeBrush(string key, Color color)
    {
        Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private static void SetThemeBrush(object key, Color color)
    {
        Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private void ApplyWindowChromeTheme(bool useDarkTheme)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var darkModeFlag = useDarkTheme ? 1 : 0;

        var result = DwmSetWindowAttribute(windowHandle, DwmUseImmersiveDarkMode, ref darkModeFlag, sizeof(int));
        if (result != 0)
        {
            _ = DwmSetWindowAttribute(windowHandle, DwmUseImmersiveDarkModeBefore20H1, ref darkModeFlag, sizeof(int));
        }

        if (useDarkTheme)
        {
            var darkCaptionColor = ToColorRef(Color.FromRgb(22, 25, 31));
            var darkBorderColor = ToColorRef(Color.FromRgb(22, 25, 31));
            var lightTextColor = ToColorRef(Color.FromRgb(243, 244, 246));

            _ = DwmSetWindowAttribute(windowHandle, DwmCaptionColor, ref darkCaptionColor, sizeof(uint));
            _ = DwmSetWindowAttribute(windowHandle, DwmBorderColor, ref darkBorderColor, sizeof(uint));
            _ = DwmSetWindowAttribute(windowHandle, DwmTextColor, ref lightTextColor, sizeof(uint));
            return;
        }

        var defaultColor = DwmColorDefault;
        _ = DwmSetWindowAttribute(windowHandle, DwmCaptionColor, ref defaultColor, sizeof(uint));
        _ = DwmSetWindowAttribute(windowHandle, DwmBorderColor, ref defaultColor, sizeof(uint));
        _ = DwmSetWindowAttribute(windowHandle, DwmTextColor, ref defaultColor, sizeof(uint));
    }

    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;
    private const uint DwmColorDefault = 0xFFFFFFFF;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);

    private static uint ToColorRef(Color color)
    {
        return (uint)(color.R | (color.G << 8) | (color.B << 16));
    }

    private void LoadSettings()
    {
        _settings = _settingsService.Load();

        RefreshRecentStarPaths(_settings.RecentStarRootPaths ?? []);

        if (!string.IsNullOrWhiteSpace(_settings.LastStarRootPath) && Directory.Exists(_settings.LastStarRootPath))
        {
            _selectedStarRoot = _settings.LastStarRootPath;
            StarPathTextBox.Text = _selectedStarRoot;
            if (RecentStarPaths.Contains(_selectedStarRoot))
            {
                RecentStarPathsComboBox.SelectedItem = _selectedStarRoot;
            }

            LogStatus("Loaded last STAR folder from settings. Running initial scan.");
            _ = Dispatcher.BeginInvoke(async () => await ScanSelectedStarRootAsync(), DispatcherPriority.Background);
        }

        var themeName = string.IsNullOrWhiteSpace(_settings.ThemeName) ? "System" : _settings.ThemeName;
        SetThemeSelection(themeName);
        ApplyTheme(themeName);
        UpdateCoagulatorStatusText();
    }

    private void SetThemeSelection(string themeName)
    {
        foreach (var item in ThemeComboBox.Items)
        {
            if (item is not ComboBoxItem comboBoxItem)
            {
                continue;
            }

            if (!string.Equals(comboBoxItem.Content?.ToString(), themeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ThemeComboBox.SelectedItem = comboBoxItem;
            return;
        }

        ThemeComboBox.SelectedIndex = 0;
    }

    private void UpdateCoagulatorStatusText()
    {
        CoagulatorStatusTextBlock.Text = _coagulatorProcessService.IsRunning
            ? "Coagulator: Running"
            : "Coagulator: Stopped";
    }

    private void AddRecentStarPath(string path)
    {
        var fullPath = Path.GetFullPath(path);

        var existing = RecentStarPaths
            .FirstOrDefault(existingPath => string.Equals(existingPath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            _ = RecentStarPaths.Remove(existing);
        }

        RecentStarPaths.Insert(0, fullPath);

        while (RecentStarPaths.Count > MaxRecentStarPaths)
        {
            RecentStarPaths.RemoveAt(RecentStarPaths.Count - 1);
        }

        _settings.LastStarRootPath = fullPath;
        _settings.RecentStarRootPaths = RecentStarPaths.ToList();
        _settingsService.Save(_settings);

        _isUpdatingRecentStarPathSelection = true;
        try
        {
            RecentStarPathsComboBox.SelectedItem = fullPath;
        }
        finally
        {
            _isUpdatingRecentStarPathSelection = false;
        }
    }

    private void RefreshRecentStarPaths(IEnumerable<string> paths)
    {
        RecentStarPaths.Clear();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                continue;
            }

            if (RecentStarPaths.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            RecentStarPaths.Add(path);

            if (RecentStarPaths.Count >= MaxRecentStarPaths)
            {
                break;
            }
        }
    }

    private void ClearActivityLogButton_OnClick(object sender, RoutedEventArgs e)
    {
        ActivityLogTextBox.Clear();
        LogStatus("Activity log cleared.", includeInLog: false);
    }

    private void ProviderIniEditorTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingProviderIniContent || string.IsNullOrWhiteSpace(_selectedProviderIniPath))
        {
            return;
        }

        if (_hasUnsavedProviderIniChanges)
        {
            return;
        }

        _hasUnsavedProviderIniChanges = true;
        UpdateProviderIniPathLabel();
    }

    private void UpdateProviderIniPathLabel()
    {
        if (string.IsNullOrWhiteSpace(_selectedProviderIniPath))
        {
            return;
        }

        SelectedProviderIniPathTextBlock.Text = _hasUnsavedProviderIniChanges
            ? $"INI file: {_selectedProviderIniPath} (unsaved changes)"
            : $"INI file: {_selectedProviderIniPath}";
    }

    private bool ConfirmPendingProviderIniChanges(string actionDescription)
    {
        if (!_hasUnsavedProviderIniChanges)
        {
            return true;
        }

        var decision = MessageBox.Show(
            this,
            $"You have unsaved INI changes. Save before {actionDescription}?",
            "Unsaved INI changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        if (decision == MessageBoxResult.Yes)
        {
            SaveProviderIniButton_OnClick(this, new RoutedEventArgs());
            return !_hasUnsavedProviderIniChanges;
        }

        if (decision == MessageBoxResult.No)
        {
            _hasUnsavedProviderIniChanges = false;
            return true;
        }

        return false;
    }

    private void LogStatus(string message, bool isError = false, bool includeInLog = true)
    {
        StatusTextBlock.Text = message;

        if (!includeInLog)
        {
            return;
        }

        var severity = isError ? "ERROR" : "INFO";
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {severity}: {message}";

        if (string.IsNullOrWhiteSpace(ActivityLogTextBox.Text))
        {
            ActivityLogTextBox.Text = line;
        }
        else
        {
            ActivityLogTextBox.AppendText(Environment.NewLine + line);
        }

        TrimActivityLogToLimit();
        ActivityLogTextBox.ScrollToEnd();
    }

    private void SetScanningUiState(bool isScanning)
    {
        BrowseButton.IsEnabled = !isScanning;
        ScanButton.IsEnabled = !isScanning;
        RecentStarPathsComboBox.IsEnabled = !isScanning;
    }

    private void ShowScanProgress(double value, string message)
    {
        ScanProgressBorder.Visibility = Visibility.Visible;
        ScanProgressBar.Value = value;
        ScanProgressTextBlock.Text = $"{message} {value:0}%";
    }

    private async Task HideScanProgressAfterDelayAsync(int scanGeneration)
    {
        await Task.Delay(TimeSpan.FromSeconds(3));

        if (scanGeneration != _scanProgressGeneration || _isScanning)
        {
            return;
        }

        ScanProgressBorder.Visibility = Visibility.Collapsed;
        ScanProgressBar.Value = 0;
        ScanProgressTextBlock.Text = "Scanning STAR setup...";
    }

    private void TrimActivityLogToLimit()
    {
        var lines = ActivityLogTextBox.Text
            .Split(Environment.NewLine, StringSplitOptions.None);

        if (lines.Length <= MaxActivityLogLines)
        {
            return;
        }

        ActivityLogTextBox.Text = string.Join(Environment.NewLine, lines[^MaxActivityLogLines..]);
    }


    private static bool IsSystemInDarkMode()
    {
        try
        {
            const string personalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            const string appsUseLightThemeValue = "AppsUseLightTheme";

            var appsUseLightTheme = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(personalizeKeyPath)?
                .GetValue(appsUseLightThemeValue);

            return appsUseLightTheme is int intValue && intValue == 0;
        }
        catch
        {
            return false;
        }
    }
}