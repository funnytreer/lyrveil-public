using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LyricsXQuiet.App.Services;
using LyricsXQuiet.App.State;
using LyricsXQuiet.App.Windows;
using LyricsXQuiet.Core.Lyrics;
using LyricsXQuiet.Core.Lyrics.Providers;
using LyricsXQuiet.Core.Playback;
using LyricsXQuiet.Core.Settings;
using LyricsXQuiet.Platform.Windows.Companion;
using LyricsXQuiet.Platform.Windows.Interop;
using LyricsXQuiet.Platform.Windows.Playback;

namespace LyricsXQuiet.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = PlayerMonitor.MainMutexName;
    private const string ShowExistingInstanceEventName = "Local\\Lyrveil.DesktopLyrics.ShowSettings";
    private readonly SettingsStore _settingsStore = new();
    internal AppHotKeys? HotKeys { get; private set; }
    internal ThemeManager? Theme { get; private set; }
    private SettingsPersistenceService? _settingsPersistence;
    private readonly DispatcherTimer _companionTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };
    private readonly SemaphoreSlim _companionLaunchGate = new(1, 1);
    private readonly object _playerMonitorRegistrationLock = new();
    private readonly HashSet<string> _shortcutCompanionProcessNames =
        new(StringComparer.OrdinalIgnoreCase);
    private Task _playerMonitorRegistrationTask = Task.CompletedTask;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showExistingInstanceEvent;
    private RegisteredWaitHandle? _showExistingInstanceRegistration;
    private SingleInstanceCompanionChannel? _singleInstanceChannel;
    private string? _companionProcessName;
    private string? _companionExecutablePath;
    private MusicPlayerDefinition? _companionPlayer;
    private bool _monitorManaged;
    private readonly CompanionExitPolicy _companionExitPolicy = new(
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromMinutes(5));
    private AppSettings? _settings;
    private IPlaybackService? _playbackService;
    private GsmTcPlaybackService? _nativePlaybackService;
    private SwitchableNetEaseBridgeSnapshotSource? _netEaseBridgeSource;
    private NetEaseBridgeDiscoveryMonitor? _netEaseBridgeDiscovery;
    private readonly List<IDisposable> _lyricsProviders = [];
    private AppController? _controller;
    private OverlayWindow? _overlayWindow;
    private QuickPanelWindow? _quickPanelWindow;
    private SettingsWindow? _settingsWindow;
    private TrayContextMenu? _trayContextMenu;
    private TrayIconService? _trayIcon;
    private bool _shuttingDown;
    private bool _showSettingsRequested;

    protected override async void OnStartup(StartupEventArgs args)
    {
        // Companion shortcuts intentionally look like their player on the
        // desktop. Give the running Lyrveil process its own shell identity
        // before any WPF window is created, so Settings never inherits the
        // launcher player's taskbar icon or grouping.
        _ = DesktopAppIdentity.TryApply();
        base.OnStartup(args);
        var companionPath = GetOptionValue(args.Args, "--companion");
        var companionAppUserModelId = GetOptionValue(args.Args, "--companion-aumid");
        var companionPlayerPath = GetOptionValue(args.Args, "--companion-player-path");
        var enableNetEaseProgressBridge = args.Args.Any(argument =>
            string.Equals(
                argument,
                "--enable-netease-progress-bridge",
                StringComparison.OrdinalIgnoreCase));
        _monitorManaged = args.Args.Any(argument =>
            string.Equals(argument, "--monitor-managed", StringComparison.OrdinalIgnoreCase));
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                if (companionPath is not null)
                {
                    if (!CompanionLaunchRequest.TryCreate(
                            companionPath,
                            enableNetEaseProgressBridge,
                            out var request))
                    {
                        throw new InvalidOperationException("快捷方式中的播放器路径无效。");
                    }

                    var result = await SingleInstanceCompanionChannel.SendAsync(
                        SingleInstanceCompanionChannel.GetCurrentSessionPipeName(),
                        request!,
                        TimeSpan.FromSeconds(3));
                    if (result is CompanionLaunchResult.Rejected or CompanionLaunchResult.Failed)
                    {
                        throw new InvalidOperationException(
                            "正在运行的词间没有接受这次播放器启动请求。");
                    }
                }
                else if (companionAppUserModelId is not null)
                {
                    _ = StartPackagedCompanionPlayer(companionAppUserModelId);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidOperationException
                    or OperationCanceledException
                    or TimeoutException
                    or UnauthorizedAccessException)
            {
                System.Windows.MessageBox.Show(
                    $"无法启动播放器。\n\n{exception.Message}",
                    "词间 · Lyrveil",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                if (companionPath is null && companionAppUserModelId is null && !_monitorManaged)
                {
                    SignalExistingInstanceToShowSettings();
                }

                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                Shutdown();
            }

            return;
        }

        _showExistingInstanceEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ShowExistingInstanceEventName);
        _showExistingInstanceRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showExistingInstanceEvent,
            (_, _) => OnShowExistingInstanceRequested(),
            null,
            Timeout.Infinite,
            false);

        _netEaseBridgeSource = new SwitchableNetEaseBridgeSnapshotSource();
        _singleInstanceChannel = new SingleInstanceCompanionChannel(
            SingleInstanceCompanionChannel.GetCurrentSessionPipeName(),
            HandleForwardedCompanionLaunchAsync);
        _singleInstanceChannel.Start();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        _companionTimer.Tick += OnCompanionTimer;

        try
        {
            // Inspecting another process is optional and can be slow on some
            // NetEase builds. Run it independently so it can never delay the
            // tray icon, settings window, or every other supported player.
            _netEaseBridgeDiscovery = new NetEaseBridgeDiscoveryMonitor(
                NetEaseCdpPortDiscovery.TryFindExplicitLoopbackPort,
                _netEaseBridgeSource.ConfigurePortAsync);
            _netEaseBridgeDiscovery.Start();

            if (companionPath is not null || companionAppUserModelId is not null)
            {
                if (companionPath is not null)
                {
                    if (!CompanionLaunchRequest.TryCreate(
                            companionPath,
                            enableNetEaseProgressBridge,
                            out var request))
                    {
                        throw new InvalidOperationException("快捷方式中的播放器路径无效。");
                    }

                    _ = await LaunchCompanionAsync(request!, CancellationToken.None);
                }
                else
                {
                    _companionExecutablePath = companionPlayerPath;
                    _companionPlayer = MusicPlayerCatalog.Identify(companionAppUserModelId);
                    _companionProcessName = StartPackagedCompanionPlayer(companionAppUserModelId!);
                    _shortcutCompanionProcessNames.Add(_companionProcessName);
                    _companionTimer.Start();
                }
            }

            _settings = await _settingsStore.LoadAsync();
            try
            {
                // Persist schema migrations before subscribing to changes. A
                // monitor-managed launch may otherwise keep running for days,
                // leaving the old selection on disk until a clean shutdown.
                _settingsStore.SaveNow(_settings);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // The in-memory migration is still valid for this session. The
                // ordinary save timer or shutdown path can retry later.
            }

            Theme = new ThemeManager(this, _settings);
            MusicPlayerCatalog.ConfigureLocalPlayer(_settings.LocalPlayerExecutablePath);
            _settings.PropertyChanged += OnSettingsPropertyChanged;
            _settingsPersistence = new SettingsPersistenceService(
                _settingsStore, _settings, Dispatcher, QueuePlayerMonitorRegistration);
            UpdateCompanionTimer();

            var state = new AppState();
            var viewModel = new AppViewModel(_settings, state);
            var cache = new LyricsCache(() => _settings.LyricsDirectory);
            var providers = new ConfigurableLyricsProvider[]
            {
                new(new QqMusicProvider(), () => _settings.EnableQqMusicLyrics),
                new(new NetEaseProvider(), () => _settings.EnableNetEaseLyrics),
                new(new KugouProvider(), () => _settings.EnableKugouLyrics),
                new(new LrclibProvider(), () => _settings.EnableLrclibLyrics)
            };
            _lyricsProviders.AddRange(providers);
            var resolver = new LyricsResolver(
                cache,
                providers,
                () => _settings.ProgressiveHighlight);
            _nativePlaybackService = new GsmTcPlaybackService();
            _playbackService = new EnhancedNetEasePlaybackService(
                _nativePlaybackService,
                _netEaseBridgeSource);
            _overlayWindow = new OverlayWindow(_settings);
            _controller = new AppController(
                _playbackService,
                resolver,
                _settings,
                state,
                _overlayWindow);
            HotKeys = new AppHotKeys(_settings, HandleHotKey);
            _settingsWindow = new SettingsWindow(
                viewModel,
                _controller,
                cache,
                () => _overlayWindow?.ResetToDefaultPosition(),
                () => _settingsPersistence.SaveAsync());
            _quickPanelWindow = new QuickPanelWindow(viewModel, _controller, ShowSettings);
            _trayContextMenu = new TrayContextMenu(
                _settings,
                ShowQuickPanel,
                ShowSettings,
                () => _ = ShutdownAsync(manualExit: true));
            _trayIcon = new TrayIconService(
                Theme,
                ToggleQuickPanel,
                ShowTrayMenu);

            _overlayWindow.Show();
            _overlayWindow.Hide();
            _controller.Start();
            StartNetEaseRecovery(state);

            var demoMode = args.Args.Any(argument =>
                string.Equals(argument, "--demo", StringComparison.OrdinalIgnoreCase));
            if (demoMode)
            {
                _controller.StartDemo();
            }

            // A slow or temporarily unavailable Windows media-session service
            // must not hold back the surface the user explicitly requested.
            // The bound status fields update automatically when playback
            // discovery completes.
            if (args.Args.Any(argument =>
                string.Equals(argument, "--show-panel", StringComparison.OrdinalIgnoreCase)))
            {
                ShowQuickPanel();
            }

            if (args.Args.Any(argument =>
                string.Equals(argument, "--show-settings", StringComparison.OrdinalIgnoreCase)))
            {
                ShowSettings();
            }

            QueuePlayerMonitorRegistration();

            if (!demoMode)
            {
                try
                {
                    await _playbackService.StartAsync();
                }
                catch (UnauthorizedAccessException)
                {
                    state.PlayerDisplay = "无法读取 Windows 媒体会话";
                    state.LyricsStatus = "请检查系统的媒体控制权限";
                }
            }

            if (_showSettingsRequested)
            {
                _showSettingsRequested = false;
                ShowSettings();
            }

        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"词间启动失败。\n\n{exception.Message}",
                "词间 · Lyrveil",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await StopNetEaseRecoveryAsync();
            if (_singleInstanceChannel is not null)
            {
                await _singleInstanceChannel.DisposeAsync();
                _singleInstanceChannel = null;
            }

            if (_netEaseBridgeDiscovery is not null)
            {
                await _netEaseBridgeDiscovery.DisposeAsync();
                _netEaseBridgeDiscovery = null;
            }

            if (_playbackService is null && _netEaseBridgeSource is not null)
            {
                await _netEaseBridgeSource.DisposeAsync();
                _netEaseBridgeSource = null;
            }

            Shutdown(-1);
        }
    }

    private void HandleHotKey(string action)
    {
        if (_shuttingDown || _settings is null) return;
        switch (action)
        {
            case "ToggleLyrics": _settings.ShowLyrics = !_settings.ShowLyrics; break;
            case "ToggleLock": _settings.Locked = !_settings.Locked; break;
            case "ToggleQuickPanel":
                _trayContextMenu?.CloseMenu();
                if (_quickPanelWindow?.IsVisible == true) _quickPanelWindow.HidePanel();
                else _quickPanelWindow?.ShowPanel();
                break;
            case "OpenSearch":
                if (_controller is not null) LyricsSearchWindow.Open(_controller);
                break;
            case "OffsetForward":
            case "OffsetBackward":
                if (_controller?.CurrentTrack is { IsUsable: true } track)
                    _settings.SetTrackLyricOffsetMilliseconds(track, _settings.GetTrackLyricOffsetMilliseconds(track)
                        + (action == "OffsetForward" ? 100 : -100));
                break;
        }
    }

    private void ShowQuickPanel()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _trayContextMenu?.CloseMenu();
            _quickPanelWindow?.ShowPanel();
        });
    }

    private void ToggleQuickPanel()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _trayContextMenu?.CloseMenu();
            _quickPanelWindow?.TogglePanel();
        });
    }

    private void ShowTrayMenu()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _quickPanelWindow?.HidePanel();
            _trayContextMenu?.ShowAtCursor();
        });
    }

    private void ShowSettings()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _trayContextMenu?.CloseMenu();
            _settingsWindow?.ShowWindow();
        });
    }

    private void OnShowExistingInstanceRequested()
    {
        _showSettingsRequested = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (_settingsWindow is null)
            {
                return;
            }

            _showSettingsRequested = false;
            ShowSettings();
        });
    }

    private static void SignalExistingInstanceToShowSettings()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ShowExistingInstanceEventName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppSettings.ExitWhenPlayersClose)) UpdateCompanionTimer();
        if (string.Equals(
                args.PropertyName,
                nameof(AppSettings.LocalPlayerExecutablePath),
                StringComparison.Ordinal))
        {
            MusicPlayerCatalog.ConfigureLocalPlayer(_settings?.LocalPlayerExecutablePath);
            _nativePlaybackService?.RefreshConfiguredPlayers();
        }

    }

    private void UpdateCompanionTimer()
    {
        if (_settings?.ExitWhenPlayersClose == true) _companionTimer.Start();
        else _companionTimer.Stop();
    }

    private void QueuePlayerMonitorRegistration()
    {
        var settings = _settings;
        if (settings is null)
        {
            return;
        }

        var enabled = NeedsPlayerMonitor(settings);
        _ = EnqueuePlayerMonitorRegistration(enabled, notifyOnFailure: true);
    }

    private Task EnqueuePlayerMonitorRegistration(
        bool enabled,
        bool notifyOnFailure,
        bool allowDuringShutdown = false)
    {
        lock (_playerMonitorRegistrationLock)
        {
            if (_shuttingDown && !allowDuringShutdown)
            {
                return _playerMonitorRegistrationTask;
            }

            // Keep registration updates ordered. In particular, the final update
            // queued by ShutdownAsync must run after an earlier 600 ms debounced
            // update that may already be waiting on the thread pool.
            _playerMonitorRegistrationTask = _playerMonitorRegistrationTask
                .ContinueWith(
                    _ => ApplyPlayerMonitorRegistration(enabled, notifyOnFailure),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
            return _playerMonitorRegistrationTask;
        }
    }

    private Task ApplyPlayerMonitorRegistration(bool enabled, bool notifyOnFailure)
    {
        try
        {
            PlayerMonitorRegistrationService.Apply(enabled);
        }
        catch (Exception exception)
        {
            if (notifyOnFailure && !_shuttingDown)
            {
                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_settingsWindow?.IsVisible != true || _shuttingDown)
                        {
                            return;
                        }

                        System.Windows.MessageBox.Show(
                            _settingsWindow,
                            $"无法更新播放器监听设置。词间仍可正常使用，播放器需要手动启动。\n\n{exception.Message}",
                            "词间 · Lyrveil",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    });
                }
                catch (InvalidOperationException)
                {
                    // The dispatcher can already be shutting down. Registration
                    // failure must never keep the process alive at that point.
                }
            }

            Trace.TraceWarning(
                "Failed to apply player-monitor registration ({0}): {1}",
                enabled,
                exception);
        }

        return Task.CompletedTask;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        System.Windows.MessageBox.Show(
            $"应用遇到了一个问题，但会尽量继续运行。\n\n{args.Exception.Message}",
            "词间 · Lyrveil",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        args.Handled = true;
    }

    private async void OnCompanionTimer(object? sender, EventArgs args)
    {
        if (_recoveringNetEase) return;
        if (_settings?.ExitWhenPlayersClose == false)
        {
            return;
        }

        var configurationWindowOpen = _settingsWindow?.IsVisible == true
            || _quickPanelWindow?.IsVisible == true;
        if (IsAnyCompanionPlayerRunning() is not bool playerRunning) return;
        var updateActivityDetected = !_monitorManaged
            && !playerRunning
            && !_companionExitPolicy.IsWaitingForUpdate
            && _companionPlayer is not null
            && CompanionUpdateDetectionService.IsUpdateActivityPresent(
                _companionPlayer,
                _companionExecutablePath);
        if (_companionExitPolicy.ShouldExit(
            playerRunning,
            configurationWindowOpen,
            updateActivityDetected,
            DateTimeOffset.Now,
            requireObservedPlayer: !_monitorManaged && _companionProcessName is null))
        {
            await ShutdownAsync();
        }
    }

    private async Task<CompanionLaunchResult> HandleForwardedCompanionLaunchAsync(
        CompanionLaunchRequest request,
        CancellationToken cancellationToken)
    {
        var operation = Dispatcher.InvokeAsync(
            () => LaunchCompanionAsync(request, cancellationToken),
            DispatcherPriority.Send,
            cancellationToken);
        return await await operation.Task;
    }

    private async Task<CompanionLaunchResult> LaunchCompanionAsync(
        CompanionLaunchRequest request,
        CancellationToken cancellationToken)
    {
        if (!CompanionLaunchRequest.TryCreate(
                request.PlayerPath,
                request.EnableNetEaseProgressBridge,
                out var validatedRequest))
        {
            return CompanionLaunchResult.Rejected;
        }

        await _companionLaunchGate.WaitAsync(cancellationToken);
        try
        {
            return await LaunchValidatedCompanionAsync(validatedRequest!, cancellationToken);
        }
        finally
        {
            _companionLaunchGate.Release();
        }
    }

    private async Task<CompanionLaunchResult> LaunchValidatedCompanionAsync(
        CompanionLaunchRequest request,
        CancellationToken cancellationToken)
    {
        var resolvedPath = request.PlayerPath;
        var processName = Path.GetFileNameWithoutExtension(resolvedPath);
        var player = MusicPlayerCatalog.Identify(processName)
            ?? throw new InvalidOperationException("这个播放器不受伴随启动支持。");
        var existingProcesses = Process.GetProcessesByName(processName);
        try
        {
            _companionExecutablePath = resolvedPath;
            _companionPlayer = player;
            _companionProcessName = processName;
            _shortcutCompanionProcessNames.Add(processName);
            _companionTimer.Start();
            if (CompanionProcessLifetime.HasRunningProcess(existingProcesses))
            {
                if (request.EnableNetEaseProgressBridge
                    && string.Equals(player.Id, "netease", StringComparison.OrdinalIgnoreCase))
                {
                    await TryConfigureExistingNetEaseBridgeAsync(
                        existingProcesses,
                        cancellationToken);
                }

                // Never restart a running player. If this instance was previously
                // launched by us, its existing bridge remains active; otherwise the
                // native Windows media-session path continues as the safe fallback.
                return CompanionLaunchResult.AlreadyRunning;
            }

            var startInfo = new ProcessStartInfo(resolvedPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(resolvedPath)!
            };
            int? cdpPort = null;
            if (request.EnableNetEaseProgressBridge
                && string.Equals(player.Id, "netease", StringComparison.OrdinalIgnoreCase))
            {
                cdpPort = AllocateLoopbackPort();
                startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
                startInfo.ArgumentList.Add($"--remote-debugging-port={cdpPort.Value}");
                await (_netEaseBridgeSource
                    ?? throw new InvalidOperationException("网易云精确同步服务尚未准备好。"))
                    .ConfigurePortAsync(cdpPort.Value, cancellationToken);
            }

            try
            {
                _ = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Windows 没有返回播放器进程。");
            }
            catch
            {
                if (cdpPort is not null && _netEaseBridgeSource is not null)
                {
                    await _netEaseBridgeSource.DisableAsync(CancellationToken.None);
                }

                throw;
            }

            return CompanionLaunchResult.Started;
        }
        finally
        {
            foreach (var process in existingProcesses)
            {
                process.Dispose();
            }
        }
    }

    private async Task TryConfigureExistingNetEaseBridgeAsync(
        IEnumerable<Process>? processes = null,
        CancellationToken cancellationToken = default)
    {
        var source = _netEaseBridgeSource;
        if (source is null)
        {
            return;
        }

        int? port;
        try
        {
            port = processes is null
                ? NetEaseCdpPortDiscovery.TryFindExplicitLoopbackPort()
                : NetEaseCdpPortDiscovery.TryFindExplicitLoopbackPort(processes);
        }
        catch
        {
            return;
        }

        if (port is not null)
        {
            await source.ConfigurePortAsync(port.Value, cancellationToken);
        }
    }

    private static int AllocateLoopbackPort()
    {
        // Let Windows select an unpredictable free ephemeral port. The reservation
        // is released immediately before NetEase starts, so the port is never fixed
        // or persisted and is limited to the lifetime of that player process.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string StartPackagedCompanionPlayer(string appUserModelId)
    {
        var player = MusicPlayerCatalog.Identify(appUserModelId)
            ?? throw new InvalidOperationException("这个商店应用不是当前支持的音乐播放器。");
        var existingProcesses = Process.GetProcessesByName(player.ProcessName);
        try
        {
            if (existingProcesses.Length == 0)
            {
                var startInfo = new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = true
                };
                startInfo.ArgumentList.Add($"shell:AppsFolder\\{appUserModelId}");
                _ = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Windows 没有启动商店播放器。");
            }
        }
        finally
        {
            foreach (var process in existingProcesses)
            {
                process.Dispose();
            }
        }

        return player.ProcessName;
    }

    private bool? IsAnyCompanionPlayerRunning()
    {
        // The monitoring selection controls which players may wake Lyrveil in
        // the background. It must not narrow the lifetime check: after a
        // shortcut-launched player exits, another supported player may still be
        // open and should continue using the same Lyrveil instance.
        var processNames = MusicPlayerCatalog.DetectablePlayers
            .Select(player => player.ProcessName)
            .Concat(_shortcutCompanionProcessNames)
            .Append(_companionProcessName ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var running = PlayerProcessSnapshot.ReadRunning(processNames);
        return running is null ? null : running.Count > 0;
    }

    private static string? GetOptionValue(IReadOnlyList<string> arguments, string option)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], option, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private async Task ShutdownAsync(bool manualExit = false)
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        using var manualExitSignal = manualExit && _settings is not null && NeedsPlayerMonitor(_settings)
            ? new EventWaitHandle(false, EventResetMode.AutoReset, PlayerMonitor.ManualExitEventName)
            : null;
        manualExitSignal?.Set();
        var failures = new List<Exception>();
        var settings = _settings;
        var finalMonitorRegistration = Task.CompletedTask;

        void Attempt(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        async Task AttemptAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        try
        {
            Attempt(() => _settingsWindow?.CommitPendingEdits());
            Attempt(() => _settingsPersistence?.Dispose());
            Attempt(() => Theme?.Dispose());
            Attempt(() => HotKeys?.Dispose());
            Attempt(() => _companionTimer.Stop());
            Attempt(() => _companionTimer.Tick -= OnCompanionTimer);
            await AttemptAsync(StopNetEaseRecoveryAsync);

            // Append the in-memory state immediately after stopping the debounce
            // timer. The ordered registration queue makes this the last applied
            // state even if the user exits within 600 ms of changing the option.
            if (settings is not null)
            {
                Attempt(() => finalMonitorRegistration = EnqueuePlayerMonitorRegistration(
                    NeedsPlayerMonitor(settings),
                    notifyOnFailure: false,
                    allowDuringShutdown: true));
            }

            var showExistingInstanceRegistration = _showExistingInstanceRegistration;
            _showExistingInstanceRegistration = null;
            if (showExistingInstanceRegistration is not null)
            {
                Attempt(() => showExistingInstanceRegistration.Unregister(null));
            }

            var showExistingInstanceEvent = _showExistingInstanceEvent;
            _showExistingInstanceEvent = null;
            if (showExistingInstanceEvent is not null)
            {
                Attempt(showExistingInstanceEvent.Dispose);
            }

            var singleInstanceChannel = _singleInstanceChannel;
            _singleInstanceChannel = null;
            if (singleInstanceChannel is not null)
            {
                await AttemptAsync(async () => await singleInstanceChannel.DisposeAsync());
            }

            if (settings is not null)
            {
                Attempt(() => settings.PropertyChanged -= OnSettingsPropertyChanged);
                await AttemptAsync(() => _settingsStore.SaveAsync(settings));
            }

            // Registration is independent from settings persistence. A locked or
            // unwritable settings file must not leave the Windows monitor entry in
            // the opposite state from the option the user just selected.
            await AttemptAsync(() => finalMonitorRegistration);

            var controller = _controller;
            _controller = null;
            if (controller is not null)
            {
                Attempt(controller.Dispose);
            }

            var trayIcon = _trayIcon;
            _trayIcon = null;
            if (trayIcon is not null)
            {
                Attempt(trayIcon.Dispose);
            }

            var trayContextMenu = _trayContextMenu;
            _trayContextMenu = null;
            if (trayContextMenu is not null)
            {
                Attempt(trayContextMenu.Dispose);
            }

            var quickPanelWindow = _quickPanelWindow;
            _quickPanelWindow = null;
            if (quickPanelWindow is not null)
            {
                Attempt(quickPanelWindow.ClosePermanently);
            }

            var settingsWindow = _settingsWindow;
            _settingsWindow = null;
            if (settingsWindow is not null)
            {
                Attempt(settingsWindow.ClosePermanently);
            }

            var overlayWindow = _overlayWindow;
            _overlayWindow = null;
            if (overlayWindow is not null)
            {
                Attempt(overlayWindow.Close);
            }

            var netEaseBridgeDiscovery = _netEaseBridgeDiscovery;
            _netEaseBridgeDiscovery = null;
            if (netEaseBridgeDiscovery is not null)
            {
                await AttemptAsync(async () => await netEaseBridgeDiscovery.DisposeAsync());
            }

            var playbackService = _playbackService;
            _playbackService = null;
            if (playbackService is not null)
            {
                // EnhancedNetEasePlaybackService owns the bridge source.
                _netEaseBridgeSource = null;
                await AttemptAsync(async () => await playbackService.DisposeAsync());
            }
            else
            {
                var netEaseBridgeSource = _netEaseBridgeSource;
                _netEaseBridgeSource = null;
                if (netEaseBridgeSource is not null)
                {
                    await AttemptAsync(async () => await netEaseBridgeSource.DisposeAsync());
                }
            }

            foreach (var provider in _lyricsProviders.ToArray())
            {
                Attempt(provider.Dispose);
            }

            _lyricsProviders.Clear();
            Attempt(() => DispatcherUnhandledException -= OnDispatcherUnhandledException);

            var singleInstanceMutex = _singleInstanceMutex;
            _singleInstanceMutex = null;
            if (singleInstanceMutex is not null)
            {
                Attempt(() =>
                {
                    try
                    {
                        singleInstanceMutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                    }
                });
                Attempt(singleInstanceMutex.Dispose);
            }
        }
        finally
        {
            foreach (var failure in failures)
            {
                Trace.TraceWarning("Shutdown cleanup step failed: {0}", failure);
            }

            Shutdown();
        }
    }
}
