using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ParrotCore
{
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpRound = 2;

        private readonly ServerHost _serverHost;
        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private bool _isApplyingSettings = true;
        private bool _hasUnsavedChangesWhileRunning = false;

        private readonly DispatcherTimer _resourceTimer;
        private readonly Process _currentProcess;
        private TimeSpan _lastCpuTime;
        private DateTime _lastSampleTime;
        private long _lastNetRequests = 0;
        private long _lastNetBytes = 0;

        private TaskCompletionSource<bool>? _modalTcs;

        public MainWindow()
        {
            AppConfig.Load();
            _serverHost = new ServerHost(Log);
            _currentProcess = Process.GetCurrentProcess();
            _lastCpuTime = _currentProcess.TotalProcessorTime;
            _lastSampleTime = DateTime.UtcNow;

            InitializeComponent();
            InitializeTrayIcon();

            LoadValuesIntoSettingsUI();
            ApplyLocalization();
            ApplyTheme(AppConfig.Current.Theme != "light");
            ApplySettingsModeUI(AppConfig.Current.IsAdvancedMode);
            UpdateDatabaseFieldsVisibility();
            UpdateActionButtonsState();
            UpdateWarningBannerState();

            PrintStartupAsciiBanner();

            _resourceTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1000)
            };
            _resourceTimer.Tick += ResourceTimer_Tick;
            _resourceTimer.Start();
            UpdateResourceMetrics();

            SizeChanged += (_, _) => UpdateRootClip();
            PreviewMouseDown += MainWindow_PreviewMouseDown;

            Loaded += async (_, _) =>
            {
                UpdateRootClip();
                _isApplyingSettings = false;
                if (AppConfig.Current.CheckUpdatesOnStartup)
                {
                    _ = Task.Run(() => AppConfig.CheckForUpdatesAsync(Log));
                }
                if (AppConfig.Current.AutoStartServerOnLaunch)
                {
                    await StartEngineAsync();
                }
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    int pref = DwmwcpRound;
                    DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref pref, sizeof(int));
                }
            }
            catch { }
        }

        private void UpdateRootClip()
        {
            if (ActualWidth > 0 && ActualHeight > 0)
            {
                RootGrid.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 12, 12);
            }
        }

        private void ResourceTimer_Tick(object? sender, EventArgs e)
        {
            UpdateResourceMetrics();
        }

        private void UpdateResourceMetrics()
        {
            try
            {
                _currentProcess.Refresh();
                var now = DateTime.UtcNow;
                var cpuNow = _currentProcess.TotalProcessorTime;
                double elapsedSec = (now - _lastSampleTime).TotalSeconds;
                if (elapsedSec <= 0.05) elapsedSec = 1.0;

                double cpuDeltaMs = (cpuNow - _lastCpuTime).TotalMilliseconds;
                double cpuPercent = (cpuDeltaMs / (elapsedSec * 1000.0 * Environment.ProcessorCount)) * 100.0;
                cpuPercent = Math.Clamp(cpuPercent, 0.0, 100.0);

                _lastCpuTime = cpuNow;
                _lastSampleTime = now;

                double ramMb = _currentProcess.WorkingSet64 / (1024.0 * 1024.0);

                long currentNetReqs = _serverHost.TotalRequests;
                long currentNetBytes = _serverHost.TotalBytesTransferred;

                long deltaReqs = Math.Max(0, currentNetReqs - _lastNetRequests);
                long deltaBytes = Math.Max(0, currentNetBytes - _lastNetBytes);
                _lastNetRequests = currentNetReqs;
                _lastNetBytes = currentNetBytes;

                long bytesPerSec = (long)(deltaBytes / elapsedSec);
                int reqsPerSec = (int)Math.Round(deltaReqs / elapsedSec);

                long activeCacheBytes = GC.GetTotalMemory(false);

                const double maxBarWidth = 248.0;
                TxtResCpuValue.Text = $"{cpuPercent:0.0}%";
                BarResCpu.Width = Math.Clamp((cpuPercent / 100.0) * maxBarWidth, 6.0, maxBarWidth);

                TxtResRamValue.Text = $"{ramMb:0.1} MB";
                BarResRam.Width = Math.Clamp((ramMb / 512.0) * maxBarWidth, 12.0, maxBarWidth);

                TxtResNetworkValue.Text = $"{FormatBytes(bytesPerSec)}/s • {reqsPerSec} req/s";
                double netRatio = bytesPerSec <= 0 ? 0.02 : Math.Min(1.0, Math.Log10(Math.Max(1, bytesPerSec)) / 7.0);
                BarResNetwork.Width = Math.Clamp(netRatio * maxBarWidth, 6.0, maxBarWidth);

                TxtResCacheValue.Text = FormatBytes(activeCacheBytes);
                double cacheRatio = Math.Min(1.0, (activeCacheBytes / (64.0 * 1024.0 * 1024.0)));
                BarResCache.Width = Math.Clamp(cacheRatio * maxBarWidth, 10.0, maxBarWidth);
            }
            catch { }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return $"{kb:0.1} KB";
            double mb = kb / 1024.0;
            return $"{mb:0.2} MB";
        }

        private Task ShowModalAlertAsync(string title, string message)
        {
            _modalTcs = new TaskCompletionSource<bool>();
            ModalTitleText.Text = title;
            ModalBodyText.Text = message;
            BtnModalYes.Visibility = Visibility.Collapsed;
            BtnModalNo.Visibility = Visibility.Collapsed;
            BtnModalOk.Visibility = Visibility.Visible;
            BtnModalOk.Content = Localization.T("ModalOk");
            ModalOverlay.Visibility = Visibility.Visible;
            return _modalTcs.Task;
        }

        private Task<bool> ShowModalConfirmAsync(string title, string message)
        {
            _modalTcs = new TaskCompletionSource<bool>();
            ModalTitleText.Text = title;
            ModalBodyText.Text = message;
            BtnModalYes.Visibility = Visibility.Visible;
            BtnModalNo.Visibility = Visibility.Visible;
            BtnModalOk.Visibility = Visibility.Collapsed;
            BtnModalYes.Content = Localization.T("ModalYes");
            BtnModalNo.Content = Localization.T("ModalNo");
            ModalOverlay.Visibility = Visibility.Visible;
            return _modalTcs.Task;
        }

        private void BtnModalOk_Click(object sender, RoutedEventArgs e)
        {
            ModalOverlay.Visibility = Visibility.Collapsed;
            _modalTcs?.TrySetResult(true);
        }

        private void BtnModalYes_Click(object sender, RoutedEventArgs e)
        {
            ModalOverlay.Visibility = Visibility.Collapsed;
            _modalTcs?.TrySetResult(true);
        }

        private void BtnModalNo_Click(object sender, RoutedEventArgs e)
        {
            ModalOverlay.Visibility = Visibility.Collapsed;
            _modalTcs?.TrySetResult(false);
        }

        private void InitializeTrayIcon()
        {
            try
            {
                System.Drawing.Icon? icon = null;
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream("ParrotCore.Parrotcore.ico");
                if (stream != null)
                {
                    icon = new System.Drawing.Icon(stream);
                }

                _trayIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = icon ?? System.Drawing.SystemIcons.Application,
                    Text = "ParrotCore 2.3 Hyacinth",
                    Visible = true
                };
                _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("ParrotCore 2.3 Hyacinth", null, (_, _) => RestoreFromTray());
                menu.Items.Add("Exit", null, async (_, _) => await ShutdownAndExitAsync());
                _trayIcon.ContextMenuStrip = menu;
            }
            catch { }
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }

        private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.XButton1 && SettingsOverlay.Visibility == Visibility.Visible)
            {
                Settings_Back_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
        }

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            bool nextDark = AppConfig.Current.Theme == "light";
            AppConfig.Current.Theme = nextDark ? "dark" : "light";
            _isApplyingSettings = true;
            CmbTheme.SelectedIndex = nextDark ? 0 : 1;
            _isApplyingSettings = false;
            ApplyTheme(nextDark);
            if (!_serverHost.IsRunning)
            {
                AppConfig.Save();
            }
            else
            {
                _hasUnsavedChangesWhileRunning = true;
                UpdateWarningBannerState();
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsOverlay.Visibility == Visibility.Visible)
            {
                Settings_Back_Click(sender, e);
                return;
            }
            UpdateWarningBannerState();
            SettingsOverlay.Visibility = Visibility.Visible;
            SettingsOverlay.IsHitTestVisible = true;
            MainDashboard.IsHitTestVisible = false;
        }

        private void Settings_Back_Click(object sender, RoutedEventArgs e)
        {
            if (!_serverHost.IsRunning)
            {
                SyncSettingsFromUIToConfigAndSave();
            }
            SettingsOverlay.Visibility = Visibility.Collapsed;
            SettingsOverlay.IsHitTestVisible = false;
            MainDashboard.IsHitTestVisible = true;
            UpdateActionButtonsState();
        }

        private void BtnModeSimple_Click(object sender, RoutedEventArgs e)
        {
            AppConfig.Current.IsAdvancedMode = false;
            ApplySettingsModeUI(false);
            HandleAutoSaveTrigger();
        }

        private void BtnModeAdvanced_Click(object sender, RoutedEventArgs e)
        {
            AppConfig.Current.IsAdvancedMode = true;
            ApplySettingsModeUI(true);
            HandleAutoSaveTrigger();
        }

        private void ApplySettingsModeUI(bool isAdvanced)
        {
            if (AdvNetworkPanel == null || AdvDbPanel == null || AdvWebsitePanel == null) return;

            var vis = isAdvanced ? Visibility.Visible : Visibility.Collapsed;
            AdvNetworkPanel.Visibility = vis;
            AdvDbPanel.Visibility = vis;
            AdvWebsitePanel.Visibility = vis;

            var primaryBrush = (System.Windows.Media.Brush)Resources["ColorPrimaryBrush"];
            var secTextBrush = (System.Windows.Media.Brush)Resources["TextSecondary"];

            if (isAdvanced)
            {
                BtnModeAdvanced.Background = primaryBrush;
                BtnModeAdvanced.Foreground = System.Windows.Media.Brushes.White;
                BtnModeSimple.Background = System.Windows.Media.Brushes.Transparent;
                BtnModeSimple.Foreground = secTextBrush;
            }
            else
            {
                BtnModeSimple.Background = primaryBrush;
                BtnModeSimple.Foreground = System.Windows.Media.Brushes.White;
                BtnModeAdvanced.Background = System.Windows.Media.Brushes.Transparent;
                BtnModeAdvanced.Foreground = secTextBrush;
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private async void Close_Click(object sender, RoutedEventArgs e)
        {
            if (AppConfig.Current.MinimizeToTrayOnClose)
            {
                Hide();
                Log("[System] Minimized to system tray.");
            }
            else
            {
                await ShutdownAndExitAsync();
            }
        }

        private void BtnGithubHnato_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://github.com/Hnato") { UseShellExecute = true });
            }
            catch { }
        }

        private async void BtnStartStop_Click(object sender, RoutedEventArgs e)
        {
            if (_serverHost.IsRunning)
                await StopEngineAsync();
            else
                await StartEngineAsync();
        }

        private async void BtnRestart_Click(object sender, RoutedEventArgs e)
        {
            await RestartEngineAsync();
        }

        private async void BtnDatabase_Click(object sender, RoutedEventArgs e)
        {
            if (!AppConfig.Current.IsConfigured || !_serverHost.DbManager.IsConnected)
            {
                SettingsTabControl.SelectedIndex = 2;
                UpdateWarningBannerState();
                SettingsOverlay.Visibility = Visibility.Visible;
                SettingsOverlay.IsHitTestVisible = true;
                MainDashboard.IsHitTestVisible = false;
            }
            else
            {
                BtnDatabase.IsEnabled = false;
                await _serverHost.DbManager.RestartConnectionAsync();
                BtnDatabase.IsEnabled = true;
                UpdateActionButtonsState();
            }
        }

        private void BtnOpenApp_Click(object sender, RoutedEventArgs e)
        {
            string url = _serverHost.GetPreferredBrowserUrl();
            try
            {
                Log($"[Browser] Opening {url}...");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log($"[Error] Could not launch browser: {ex.Message}");
            }
        }

        private async void BtnWizard_Click(object sender, RoutedEventArgs e)
        {
            await RunInteractiveProjectWizardAsync();
        }

        private async void BtnScanNetwork_Click(object sender, RoutedEventArgs e)
        {
            int port = int.TryParse(TxtPort.Text.Trim(), out int p) ? Math.Clamp(p, 1, 65535) : AppConfig.Current.Port;
            string report = ServerHost.DiagnosePortAndFirewall(port);
            Log(report);
            await ShowModalAlertAsync("ParrotCore Network Diagnostics", report);
        }

        private async void BtnTestDbConn_Click(object sender, RoutedEventArgs e)
        {
            SyncSettingsFromUIToMemory();
            bool ok = await _serverHost.DbManager.InitializeOrConnectAsync();
            UpdateActionButtonsState();
            await ShowModalAlertAsync(
                "Database Connection Test",
                ok
                    ? $"Successfully connected to {AppConfig.Current.DatabaseProvider}!"
                    : $"Could not reach {AppConfig.Current.DatabaseProvider}. Check console logs and verify your credentials or file path.");
        }

        private async void BtnInitDb_Click(object sender, RoutedEventArgs e)
        {
            SyncSettingsFromUIToMemory();
            bool ok = await _serverHost.DbManager.InitializeOrConnectAsync();
            if (ok)
            {
                _serverHost.DbManager.GenerateWebsiteDatabaseConfigFiles(AppConfig.Current);
            }
            UpdateActionButtonsState();
            UpdateResourceMetrics();
            await ShowModalAlertAsync(
                "Database Provisioning",
                ok
                    ? $"Database ({AppConfig.Current.DatabaseProvider}) initialized and adapted configuration files were generated in your website folder."
                    : "Database initialization failed. Check the live console log for details.");
        }

        private void BtnStartMariaDaemon_Click(object sender, RoutedEventArgs e)
        {
            SyncSettingsFromUIToMemory();
            _serverHost.DbManager.EnsureLocalMariaDbRunning(AppConfig.Current);
        }

        private void BtnBrowseWebsite_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = Localization.T("WebsitePathLabel"),
                InitialDirectory = AppConfig.Current.GetResolvedWebsitePath()
            };
            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                TxtWebsitePath.Text = AppConfig.ToPortablePath(dlg.FolderName, "Web Site");
                UpdateResourceMetrics();
            }
        }

        private void BtnBrowseSqlite_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = Localization.T("SqlitePath"),
                Filter = "SQLite Database (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|All Files (*.*)|*.*",
                FileName = Path.GetFileName(AppConfig.Current.GetResolvedSqlitePath()),
                InitialDirectory = Path.GetDirectoryName(AppConfig.Current.GetResolvedSqlitePath()) ?? AppConfig.ResolveEngineDataDirectory(),
                OverwritePrompt = false
            };
            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FileName))
            {
                TxtSqlitePath.Text = AppConfig.ToPortablePath(dlg.FileName, "Engine/Data/parrotcore.db");
            }
        }

        private void BtnBrowseMariaBin_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Localization.T("MariaBinPath"),
                Filter = "MariaDB / MySQL Server (mysqld.exe)|mysqld.exe|Executable Files (*.exe)|*.exe"
            };
            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FileName))
            {
                TxtMariaBinPath.Text = dlg.FileName;
            }
        }

        private void BtnBrowsePhpBin_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Localization.T("PhpBinPath"),
                Filter = "PHP Interpreter (php-cgi.exe;php.exe)|php-cgi.exe;php.exe|Executable Files (*.exe)|*.exe"
            };
            if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FileName))
            {
                TxtPhpBinPath.Text = dlg.FileName;
            }
        }

        private void BtnOpenWebFolder_Click(object sender, RoutedEventArgs e)
        {
            SyncSettingsFromUIToMemory();
            string folder = AppConfig.Current.GetResolvedWebsitePath();
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            catch { }
        }

        private async void BtnRestoreDefaultSite_Click(object sender, RoutedEventArgs e)
        {
            SyncSettingsFromUIToMemory();
            string portableWeb = AppConfig.ToPortablePath(AppConfig.Current.GetResolvedWebsitePath(), "Web Site");
            bool confirm = await ShowModalConfirmAsync(
                Localization.T("BtnRestoreDefault"),
                $"Restore the complete default starter website and guide into '{portableWeb}'?");
            if (confirm)
            {
                WebsiteWizard.ScaffoldStarterWebsite(AppConfig.Current, _serverHost.DbManager, Log, overwrite: true);
                UpdateResourceMetrics();
                await ShowModalAlertAsync(
                    Localization.T("BtnRestoreDefault"),
                    $"Default website files (index.html, guide.html, 404.html, Parrotcore.ico, CSS, JS, and images) have been restored in '{portableWeb}'.");
            }
        }

        private void ChkEnableDb_Click(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings) return;
            UpdateDatabaseFieldsVisibility();
            HandleAutoSaveTrigger();
        }

        private void CmbDbMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingSettings) return;
            _isApplyingSettings = true;
            if (CmbDbMode.SelectedIndex == 0)
            {
                CmbDbProvider.SelectedIndex = 0;
            }
            else if (CmbDbMode.SelectedIndex == 1)
            {
                if (CmbDbProvider.SelectedIndex == 0)
                    CmbDbProvider.SelectedIndex = 1;
                if (string.IsNullOrWhiteSpace(TxtDbHost.Text)) TxtDbHost.Text = "127.0.0.1";
                if (TxtDbPort.Text == "0" || string.IsNullOrWhiteSpace(TxtDbPort.Text)) TxtDbPort.Text = "3306";
            }
            else if (CmbDbMode.SelectedIndex == 2)
            {
                if (CmbDbProvider.SelectedIndex == 0)
                    CmbDbProvider.SelectedIndex = 2;
            }
            _isApplyingSettings = false;

            UpdateDatabaseFieldsVisibility();
            HandleAutoSaveTrigger();
        }

        private void CmbDbProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingSettings) return;
            _isApplyingSettings = true;

            switch (CmbDbProvider.SelectedIndex)
            {
                case 0:
                    CmbDbMode.SelectedIndex = 0;
                    break;
                case 1:
                case 2:
                case 3:
                    TxtDbPort.Text = "3306";
                    if (CmbDbMode.SelectedIndex == 0) CmbDbMode.SelectedIndex = 1;
                    break;
                case 4:
                case 5:
                    TxtDbPort.Text = "1433";
                    CmbDbMode.SelectedIndex = 2;
                    break;
                case 6:
                    TxtDbPort.Text = "5432";
                    CmbDbMode.SelectedIndex = 2;
                    break;
                case 7:
                    TxtDbPort.Text = "27017";
                    CmbDbMode.SelectedIndex = 2;
                    break;
            }

            _isApplyingSettings = false;
            UpdateDatabaseFieldsVisibility();
            HandleAutoSaveTrigger();
        }

        private void UpdateDatabaseFieldsVisibility()
        {
            if (PanelDbConfigAll == null) return;

            bool dbEnabled = ChkEnableDb.IsChecked == true;
            PanelDbConfigAll.Visibility = dbEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (!dbEnabled) return;

            int modeIndex = CmbDbMode.SelectedIndex;
            bool isSqlite = modeIndex == 0 || CmbDbProvider.SelectedIndex == 0;
            bool isLocalMaria = modeIndex == 1;

            PanelDbProviderCol.Visibility = modeIndex == 0 ? Visibility.Collapsed : Visibility.Visible;
            PanelSqliteSettings.Visibility = isSqlite ? Visibility.Visible : Visibility.Collapsed;
            PanelMariaDaemonSettings.Visibility = isLocalMaria ? Visibility.Visible : Visibility.Collapsed;
            PanelExternalDbServer.Visibility = isSqlite ? Visibility.Collapsed : Visibility.Visible;
            BtnStartMariaDaemon.Visibility = isLocalMaria ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingSettings) return;
            AppConfig.Current.Language = CmbLanguage.SelectedIndex switch
            {
                1 => "pl",
                2 => "de",
                3 => "fr",
                4 => "zh",
                5 => "ja",
                6 => "nb",
                _ => "en"
            };
            ApplyLocalization();
            HandleAutoSaveTrigger();
        }

        private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingSettings) return;
            bool isDark = CmbTheme.SelectedIndex != 1;
            AppConfig.Current.Theme = isDark ? "dark" : "light";
            ApplyTheme(isDark);
            HandleAutoSaveTrigger();
        }

        private void SettingControl_Changed(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings) return;
            HandleAutoSaveTrigger();
        }

        private void SettingCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isApplyingSettings) return;
            HandleAutoSaveTrigger();
        }

        private void SettingText_Changed(object sender, TextChangedEventArgs e)
        {
            if (_isApplyingSettings) return;
            HandleAutoSaveTrigger();
        }

        private void HandleAutoSaveTrigger()
        {
            if (_isApplyingSettings) return;

            if (_serverHost.IsRunning)
            {
                _hasUnsavedChangesWhileRunning = true;
                UpdateWarningBannerState();
                return;
            }

            SyncSettingsFromUIToConfigAndSave();
            UpdateActionButtonsState();
            UpdateWarningBannerState();
        }

        private static SolidColorBrush BrushFromHex(string hex)
        {
            return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        }

        private void UpdateWarningBannerState()
        {
            if (WarningBannerBorder == null) return;

            if (_serverHost.IsRunning)
            {
                WarningBannerBorder.Background = BrushFromHex("#28F59E0B");
                WarningBannerBorder.BorderBrush = BrushFromHex("#F59E0B");
                WarningBannerIcon.Text = "⚠";
                WarningBannerIcon.Foreground = BrushFromHex("#F59E0B");
                WarningBannerText.Text = Localization.T("ServerRunningWarning");
            }
            else
            {
                WarningBannerBorder.Background = BrushFromHex("#1E2ECC71");
                WarningBannerBorder.BorderBrush = BrushFromHex("#2ECC71");
                WarningBannerIcon.Text = "✔";
                WarningBannerIcon.Foreground = BrushFromHex("#2ECC71");
                WarningBannerText.Text = Localization.T("AutoSavedInfo");
            }
        }

        private void LoadValuesIntoSettingsUI()
        {
            _isApplyingSettings = true;
            var cfg = AppConfig.Current;

            ChkLaunchStartup.IsChecked = cfg.LaunchAtStartup;
            ChkMinimizeTray.IsChecked = cfg.MinimizeToTrayOnClose;
            ChkCheckUpdates.IsChecked = cfg.CheckUpdatesOnStartup;
            ChkAutoStartServer.IsChecked = cfg.AutoStartServerOnLaunch;

            CmbLanguage.SelectedIndex = cfg.Language switch
            {
                "pl" => 1,
                "de" => 2,
                "fr" => 3,
                "zh" => 4,
                "ja" => 5,
                "nb" => 6,
                _ => 0
            };
            CmbTheme.SelectedIndex = cfg.Theme == "light" ? 1 : 0;

            TxtPort.Text = cfg.Port.ToString();
            ChkAutoFallbackPort.IsChecked = cfg.AutoFallbackPort;
            CmbBindMode.SelectedIndex = cfg.BindMode switch
            {
                "localhost" => 1,
                "all" => 2,
                _ => 0
            };
            ChkCors.IsChecked = cfg.EnableCors;
            TxtCorsOrigins.Text = cfg.CorsAllowedOrigins;
            ChkWebSockets.IsChecked = cfg.EnableWebSockets;
            ChkReqLog.IsChecked = cfg.EnableRequestLogging;
            TxtMaxBody.Text = cfg.MaxRequestBodySizeMB.ToString();

            ChkEnableDb.IsChecked = cfg.EnableDatabase;
            CmbDbMode.SelectedIndex = cfg.DatabaseMode switch
            {
                "local_mariadb" => 1,
                "external" => 2,
                _ => 0
            };
            CmbDbProvider.SelectedIndex = cfg.DatabaseProvider switch
            {
                "XAMPP" => 1,
                "MariaDB" => 2,
                "MySQL" => 3,
                "AzureSQL" => 4,
                "SQLServer" => 5,
                "PostgreSQL" => 6,
                "MongoDB" => 7,
                _ => 0
            };
            TxtDbHost.Text = cfg.DbHost;
            TxtDbPort.Text = cfg.DbPort.ToString();
            TxtDbName.Text = cfg.DbName;
            TxtDbUser.Text = cfg.DbUser;
            TxtDbPass.Text = cfg.DbPassword;
            TxtSqlitePath.Text = cfg.SqliteFilePath;
            TxtMariaBinPath.Text = cfg.MariaDbBinPath;
            TxtCustomConn.Text = cfg.CustomConnectionString;
            ChkAutoGenConfig.IsChecked = cfg.AutoGenerateSiteDbConfig;
            ChkExposeRestApi.IsChecked = cfg.ExposeRestDbApi;

            TxtWebsitePath.Text = cfg.WebsiteRootPath;
            ChkEnablePhp.IsChecked = cfg.EnablePhpInterpreter;
            TxtPhpBinPath.Text = cfg.PhpExecutablePath;
            TxtDefaultFiles.Text = cfg.DefaultFiles;
            ChkDirBrowse.IsChecked = cfg.EnableDirectoryBrowsing;
            ChkSpaFallback.IsChecked = cfg.EnableSpaFallback;
            TxtSpaFile.Text = cfg.SpaFallbackFile;
            ChkGzip.IsChecked = cfg.EnableGzipCompression;
            ChkStaticCache.IsChecked = cfg.EnableStaticCache;
            TxtCacheAge.Text = cfg.CacheMaxAgeSeconds.ToString();
            ChkLiveWatch.IsChecked = cfg.EnableLiveReloadWatcher;
            ChkUnknownMime.IsChecked = cfg.ServeUnknownMimeTypes;
            TxtCustom404.Text = cfg.Custom404Page;
            TxtCustomHeaders.Text = string.Join("; ", cfg.CustomHeaders.Select(kv => $"{kv.Key}={kv.Value}"));

            _isApplyingSettings = false;
        }

        private void SyncSettingsFromUIToMemory()
        {
            var cfg = AppConfig.Current;

            cfg.LaunchAtStartup = ChkLaunchStartup.IsChecked == true;
            cfg.MinimizeToTrayOnClose = ChkMinimizeTray.IsChecked == true;
            cfg.CheckUpdatesOnStartup = ChkCheckUpdates.IsChecked == true;
            cfg.AutoStartServerOnLaunch = ChkAutoStartServer.IsChecked == true;

            cfg.Language = CmbLanguage.SelectedIndex switch
            {
                1 => "pl",
                2 => "de",
                3 => "fr",
                4 => "zh",
                5 => "ja",
                6 => "nb",
                _ => "en"
            };
            cfg.Theme = CmbTheme.SelectedIndex == 1 ? "light" : "dark";

            if (int.TryParse(TxtPort.Text.Trim(), out int port))
                cfg.Port = Math.Clamp(port, 1, 65535);

            cfg.AutoFallbackPort = ChkAutoFallbackPort.IsChecked == true;
            cfg.BindMode = CmbBindMode.SelectedIndex switch
            {
                1 => "localhost",
                2 => "all",
                _ => "both"
            };
            cfg.EnableCors = ChkCors.IsChecked == true;
            cfg.CorsAllowedOrigins = TxtCorsOrigins.Text.Trim();
            cfg.EnableWebSockets = ChkWebSockets.IsChecked == true;
            cfg.EnableRequestLogging = ChkReqLog.IsChecked == true;

            if (int.TryParse(TxtMaxBody.Text.Trim(), out int maxBody))
                cfg.MaxRequestBodySizeMB = Math.Clamp(maxBody, 1, 2048);

            cfg.EnableDatabase = ChkEnableDb.IsChecked == true;
            cfg.DatabaseMode = CmbDbMode.SelectedIndex switch
            {
                1 => "local_mariadb",
                2 => "external",
                _ => "local_sqlite"
            };
            cfg.DatabaseProvider = CmbDbMode.SelectedIndex == 0
                ? "SQLite"
                : CmbDbProvider.SelectedIndex switch
                {
                    1 => "XAMPP",
                    2 => "MariaDB",
                    3 => "MySQL",
                    4 => "AzureSQL",
                    5 => "SQLServer",
                    6 => "PostgreSQL",
                    7 => "MongoDB",
                    _ => "SQLite"
                };
            cfg.DbHost = TxtDbHost.Text.Trim();
            if (int.TryParse(TxtDbPort.Text.Trim(), out int dbPort))
                cfg.DbPort = Math.Clamp(dbPort, 1, 65535);
            cfg.DbName = TxtDbName.Text.Trim();
            cfg.DbUser = TxtDbUser.Text.Trim();
            cfg.DbPassword = TxtDbPass.Text;
            cfg.SqliteFilePath = TxtSqlitePath.Text.Trim();
            cfg.MariaDbBinPath = TxtMariaBinPath.Text.Trim();
            cfg.CustomConnectionString = TxtCustomConn.Text.Trim();
            cfg.AutoGenerateSiteDbConfig = ChkAutoGenConfig.IsChecked == true;
            cfg.ExposeRestDbApi = ChkExposeRestApi.IsChecked == true;

            cfg.WebsiteRootPath = TxtWebsitePath.Text.Trim();
            cfg.EnablePhpInterpreter = ChkEnablePhp.IsChecked == true;
            cfg.PhpExecutablePath = TxtPhpBinPath.Text.Trim();
            cfg.DefaultFiles = TxtDefaultFiles.Text.Trim();
            cfg.EnableDirectoryBrowsing = ChkDirBrowse.IsChecked == true;
            cfg.EnableSpaFallback = ChkSpaFallback.IsChecked == true;
            cfg.SpaFallbackFile = TxtSpaFile.Text.Trim();
            cfg.EnableGzipCompression = ChkGzip.IsChecked == true;
            cfg.EnableStaticCache = ChkStaticCache.IsChecked == true;
            if (int.TryParse(TxtCacheAge.Text.Trim(), out int cacheAge))
                cfg.CacheMaxAgeSeconds = Math.Clamp(cacheAge, 0, 31536000);
            cfg.EnableLiveReloadWatcher = ChkLiveWatch.IsChecked == true;
            cfg.ServeUnknownMimeTypes = ChkUnknownMime.IsChecked == true;
            cfg.Custom404Page = TxtCustom404.Text.Trim();

            cfg.CustomHeaders.Clear();
            foreach (var part in TxtCustomHeaders.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2)
                    cfg.CustomHeaders[kv[0].Trim()] = kv[1].Trim();
            }
        }

        private void SyncSettingsFromUIToConfigAndSave()
        {
            SyncSettingsFromUIToMemory();
            AppConfig.Save();

            if (AppConfig.Current.EnableDatabase && AppConfig.Current.AutoGenerateSiteDbConfig)
            {
                _serverHost.DbManager.GenerateWebsiteDatabaseConfigFiles(AppConfig.Current);
            }
            _hasUnsavedChangesWhileRunning = false;
        }

        public void ApplyLocalization()
        {
            bool wasApplying = _isApplyingSettings;
            _isApplyingSettings = true;

            LblTopTitle.Text = Localization.T("AppName");
            LblFooterVersion.Text = $"{Localization.T("AppName")} {Localization.T("VersionName")}";
            LblSettingsTitle.Text = Localization.T("SettingsTitle");
            LblAppSectionTitle.Text = Localization.T("AppSectionTitle");

            LblPanelControls.Text = Localization.T("PanelControls");
            LblPanelResources.Text = Localization.T("PanelResources");
            LblPanelConsole.Text = Localization.T("PanelConsole");
            LblResCpu.Text = Localization.T("ResCpu");
            LblResRam.Text = Localization.T("ResRam");
            LblResNetwork.Text = Localization.T("ResNetwork");
            LblResCache.Text = Localization.T("ResCache");

            BtnModeSimple.Content = Localization.T("ModeSimple");
            BtnModeAdvanced.Content = Localization.T("ModeAdvanced");

            TabGeneralItem.Header = Localization.T("TabGeneral");
            TabNetworkItem.Header = Localization.T("TabNetwork");
            TabDatabaseItem.Header = Localization.T("TabDatabase");
            TabWebsiteItem.Header = Localization.T("TabWebsite");

            ChkLaunchStartup.Content = Localization.T("LaunchStartup");
            ChkMinimizeTray.Content = Localization.T("MinimizeTray");
            ChkCheckUpdates.Content = Localization.T("CheckUpdates");
            ChkAutoStartServer.Content = Localization.T("AutoStartServer");
            LblLanguage.Text = Localization.T("LanguageLabel");
            LblTheme.Text = Localization.T("ThemeLabel");
            CmbThemeDarkItem.Content = Localization.T("ThemeDark");
            CmbThemeLightItem.Content = Localization.T("ThemeLight");

            LblPort.Text = Localization.T("PortLabel");
            ChkAutoFallbackPort.Content = Localization.T("AutoFallbackPort");
            LblBindMode.Text = Localization.T("BindModeLabel");
            CmbBindBothItem.Content = Localization.T("BindBoth");
            CmbBindLocalItem.Content = Localization.T("BindLocalhost");
            CmbBindAllItem.Content = Localization.T("BindAll");
            ChkCors.Content = Localization.T("EnableCors");
            LblCorsOrigins.Text = Localization.T("CorsOrigins");
            ChkWebSockets.Content = Localization.T("EnableWebSockets");
            ChkReqLog.Content = Localization.T("EnableReqLog");
            LblMaxBody.Text = Localization.T("MaxBodySize");
            BtnScanNetwork.Content = Localization.T("BtnCheckCollision");

            ChkEnableDb.Content = Localization.T("EnableDb");
            LblDbMode.Text = Localization.T("DbModeLabel");
            CmbDbModeSqliteItem.Content = Localization.T("DbModeLocalSqlite");
            CmbDbModeMariaItem.Content = Localization.T("DbModeLocalMaria");
            CmbDbModeExtItem.Content = Localization.T("DbModeExternal");
            LblDbProvider.Text = Localization.T("DbProviderLabel");
            LblDbHost.Text = Localization.T("DbHost");
            LblDbPort.Text = Localization.T("DbPort");
            LblDbName.Text = Localization.T("DbName");
            LblDbUser.Text = Localization.T("DbUser");
            LblDbPass.Text = Localization.T("DbPass");
            LblSqlitePath.Text = Localization.T("SqlitePath");
            LblMariaBinPath.Text = Localization.T("MariaBinPath");
            LblCustomConn.Text = Localization.T("CustomConnStr");
            ChkAutoGenConfig.Content = Localization.T("AutoGenDbConfig");
            ChkExposeRestApi.Content = Localization.T("ExposeRestApi");
            BtnTestDbConn.Content = Localization.T("BtnTestDb");
            BtnInitDb.Content = Localization.T("BtnCreateDb");
            BtnStartMariaDaemon.Content = Localization.T("BtnStartMaria");

            LblWebsitePath.Text = Localization.T("WebsitePathLabel");
            ChkEnablePhp.Content = Localization.T("EnablePhp");
            LblPhpBinPath.Text = Localization.T("PhpBinPath");
            LblDefaultFiles.Text = Localization.T("DefaultFilesLabel");
            ChkDirBrowse.Content = Localization.T("EnableDirBrowse");
            ChkSpaFallback.Content = Localization.T("EnableSpa");
            LblSpaFile.Text = Localization.T("SpaFallbackFile");
            ChkGzip.Content = Localization.T("EnableGzip");
            ChkStaticCache.Content = Localization.T("EnableCache");
            LblCacheAge.Text = Localization.T("CacheMaxAge");
            ChkLiveWatch.Content = Localization.T("EnableLiveWatch");
            ChkUnknownMime.Content = Localization.T("ServeUnknownMime");
            LblCustom404.Text = Localization.T("Custom404");
            LblCustomHeaders.Text = Localization.T("CustomHeaders");
            BtnRunWizardInTab.Content = Localization.T("BtnRunWizard");
            BtnRestoreDefaultSite.Content = Localization.T("BtnRestoreDefault");
            BtnOpenWebFolder.Content = Localization.T("BtnOpenFolder");

            string browseText = Localization.T("BtnBrowse");
            BtnBrowseWebsite.Content = browseText;
            BtnBrowseSqlite.Content = browseText;
            BtnBrowseMariaBin.Content = browseText;
            BtnBrowsePhpBin.Content = browseText;

            UpdateWarningBannerState();
            UpdateActionButtonsState();

            _isApplyingSettings = wasApplying;
        }

        public void ApplyTheme(bool isDark)
        {
            var winBg = BrushFromHex(isDark ? "#121915" : "#E6EFEA");
            var headerBg = BrushFromHex(isDark ? "#0D1310" : "#FFFFFF");
            var cardBg = BrushFromHex(isDark ? "#18221D" : "#FFFFFF");
            var consoleBg = BrushFromHex(isDark ? "#0A0F0D" : "#F3F8F5");
            var consoleBorder = BrushFromHex(isDark ? "#23342B" : "#8AA899");
            var consoleFg = BrushFromHex(isDark ? "#DCECE3" : "#0D1C14");
            var inputBg = BrushFromHex(isDark ? "#101714" : "#F2F7F4");
            var inputBorder = BrushFromHex(isDark ? "#2E4539" : "#7A9A89");
            var textPrim = BrushFromHex(isDark ? "#EBF4EE" : "#0D1A14");
            var textSec = BrushFromHex(isDark ? "#8FA89B" : "#3E594C");
            var borderBrush = BrushFromHex(isDark ? "#263830" : "#9BB8AA");
            var dividerBrush = BrushFromHex(isDark ? "#1E2D26" : "#CFE0D7");
            var primaryBrush = BrushFromHex(isDark ? "#2ECC71" : "#1D9E53");

            Resources["WindowBackground"] = winBg;
            Resources["HeaderBackground"] = headerBg;
            Resources["CardBackground"] = cardBg;
            Resources["ConsoleBackground"] = consoleBg;
            Resources["ConsoleBorderBrush"] = consoleBorder;
            Resources["ConsoleForeground"] = consoleFg;
            Resources["InputBackground"] = inputBg;
            Resources["InputBorderBrush"] = inputBorder;
            Resources["TextPrimary"] = textPrim;
            Resources["TextSecondary"] = textSec;
            Resources["BorderBrush"] = borderBrush;
            Resources["DividerBrush"] = dividerBrush;
            Resources["ColorPrimaryBrush"] = primaryBrush;

            Background = winBg;
            if (WindowFrame != null) WindowFrame.Background = winBg;
            if (RootGrid != null) RootGrid.Background = winBg;
            if (TitleBarBorder != null) TitleBarBorder.Background = headerBg;
            if (BottomBarBorder != null) BottomBarBorder.Background = headerBg;
            if (SettingsOverlay != null) SettingsOverlay.Background = winBg;

            if (ThemeIconText != null)
            {
                ThemeIconText.Text = isDark ? "" : "";
            }

            ApplySettingsModeUI(AppConfig.Current.IsAdvancedMode);
        }

        private void UpdateActionButtonsState()
        {
            var cfg = AppConfig.Current;

            if (_serverHost.IsRunning)
            {
                BtnStartStop.Content = Localization.T("BtnStop");
                BtnStartStop.Background = BrushFromHex("#E74C3C");
                ConsoleStatusDot.Fill = BrushFromHex("#2ECC71");
                ConsoleStatusText.Text = $"ONLINE :{_serverHost.SelectedPort}";
                ConsoleStatusText.Foreground = BrushFromHex("#2ECC71");
            }
            else
            {
                BtnStartStop.Content = Localization.T("BtnStart");
                BtnStartStop.Background = BrushFromHex("#2ECC71");
                ConsoleStatusDot.Fill = BrushFromHex("#8FA89B");
                ConsoleStatusText.Text = "STOPPED";
                ConsoleStatusText.Foreground = (System.Windows.Media.Brush)Resources["TextSecondary"];
            }

            BtnRestart.Content = Localization.T("BtnRestart");
            BtnRestart.Visibility = _serverHost.IsRunning ? Visibility.Visible : Visibility.Collapsed;

            BtnDatabase.Visibility = cfg.EnableDatabase ? Visibility.Visible : Visibility.Collapsed;
            if (cfg.EnableDatabase)
            {
                if (cfg.IsConfigured && _serverHost.DbManager.IsConnected)
                {
                    BtnDatabase.Content = Localization.T("BtnDbRestart");
                    BtnDatabase.Background = BrushFromHex("#F39C12");
                }
                else
                {
                    BtnDatabase.Content = Localization.T("BtnDbConfigure");
                    BtnDatabase.Background = BrushFromHex("#9B59B6");
                }
            }

            BtnOpenApp.Content = Localization.T("BtnOpenApp");
            BtnOpenApp.IsEnabled = _serverHost.IsRunning;

            BtnWizard.Content = Localization.T("BtnWizard");
        }

        private void PrintStartupAsciiBanner()
        {
            Log(Program.GetStartupBanner());
            Log("");
        }

        public void Log(string message)
        {
            if (ConsoleTextBox == null) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string>(Log), message);
                return;
            }
            if (ConsoleTextBox.Text.Length > 120000)
            {
                ConsoleTextBox.Text = ConsoleTextBox.Text[^60000..];
            }
            ConsoleTextBox.AppendText(message + Environment.NewLine);
            ConsoleTextBox.ScrollToEnd();
        }

        private async Task StartEngineAsync()
        {
            if (_serverHost.IsRunning) return;
            BtnStartStop.IsEnabled = false;
            try
            {
                await _serverHost.StartAsync();
            }
            catch (Exception ex)
            {
                Log($"[Engine Error] Failed to start: {ex.Message}");
            }
            finally
            {
                BtnStartStop.IsEnabled = true;
                UpdateActionButtonsState();
                UpdateWarningBannerState();
                UpdateResourceMetrics();
            }
        }

        private async Task StopEngineAsync()
        {
            if (!_serverHost.IsRunning) return;
            BtnStartStop.IsEnabled = false;
            try
            {
                await _serverHost.StopAsync();
                Log("[Engine] ParrotCore 2.3 Hyacinth stopped.");

                if (_hasUnsavedChangesWhileRunning)
                {
                    SyncSettingsFromUIToConfigAndSave();
                    Log("[Settings] Pending configuration changes saved automatically.");
                }
            }
            catch (Exception ex)
            {
                Log($"[Engine Error] Stop error: {ex.Message}");
            }
            finally
            {
                BtnStartStop.IsEnabled = true;
                UpdateActionButtonsState();
                UpdateWarningBannerState();
            }
        }

        private async Task RestartEngineAsync()
        {
            BtnRestart.IsEnabled = false;
            BtnStartStop.IsEnabled = false;
            try
            {
                await StopEngineAsync();
                await Task.Delay(250);
                await StartEngineAsync();
            }
            finally
            {
                BtnRestart.IsEnabled = true;
                BtnStartStop.IsEnabled = true;
                UpdateActionButtonsState();
                UpdateWarningBannerState();
            }
        }

        private async Task RunInteractiveProjectWizardAsync()
        {
            SyncSettingsFromUIToMemory();
            var cfg = AppConfig.Current;
            var audit = WebsiteWizard.InspectProject(cfg, Log);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Folder: {AppConfig.ToPortablePath(audit.RootPath, "Web Site")}");
            sb.AppendLine($"Total Files: {audit.TotalFiles} (HTML: {audit.HtmlFiles}, PHP: {audit.PhpFiles}, CSS: {audit.CssFiles}, JS: {audit.JsFiles})");
            sb.AppendLine($"Entry Document: {(audit.HasEntryFile ? audit.DetectedEntryFile : "MISSING")}");
            sb.AppendLine($"Database Config File: {(audit.HasDbIntegration ? "Detected" : "Not generated (Optional)")}");
            sb.AppendLine();

            if (audit.PassedChecks.Count > 0)
            {
                sb.AppendLine("Passed Checks:");
                foreach (var p in audit.PassedChecks) sb.AppendLine($"  + {p}");
            }
            if (audit.Warnings.Count > 0)
            {
                sb.AppendLine("\nWarnings / Recommendations:");
                foreach (var w in audit.Warnings) sb.AppendLine($"  ! {w}");
            }

            if (!audit.HasEntryFile)
            {
                sb.AppendLine("\nWould you like to generate a clean English starter website in this folder now?");
                bool createStarter = await ShowModalConfirmAsync("Project Inspector", sb.ToString());
                if (createStarter)
                {
                    WebsiteWizard.ScaffoldStarterWebsite(cfg, _serverHost.DbManager, Log, overwrite: true);
                    UpdateResourceMetrics();
                }
            }
            else
            {
                await ShowModalAlertAsync("Project Inspector", sb.ToString());
            }
        }

        public async Task ExternalInstallerShutdownAsync()
        {
            await ShutdownAndExitAsync();
        }

        private async Task ShutdownAndExitAsync()
        {
            try
            {
                _resourceTimer.Stop();
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
                await _serverHost.StopAsync();
            }
            catch { }
            System.Windows.Application.Current.Shutdown();
        }
    }
}
