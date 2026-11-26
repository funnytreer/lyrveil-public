using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using LyricsXQuiet.Core.Settings;

namespace LyricsXQuiet.App.Services;

/// <summary>Owns debounced writes, position durability and bounded save retries.</summary>
public sealed class SettingsPersistenceService : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(600);
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly Action _afterSave;
    private readonly DispatcherTimer _timer;
    private int _retryCount;
    private bool _disposed;

    public SettingsPersistenceService(SettingsStore store, AppSettings settings,
        Dispatcher dispatcher, Action afterSave)
    {
        _store = store;
        _settings = settings;
        _afterSave = afterSave;
        _timer = new DispatcherTimer(Debounce, DispatcherPriority.Background, OnTimer, dispatcher);
        _timer.Stop();
        settings.PropertyChanged += OnSettingsChanged;
    }

    public Task SaveAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _store.SaveNow(_settings);
        _timer.Stop();
        ResetRetry();
        _afterSave();
        return Task.CompletedTask;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        _timer.Stop();
        ResetRetry();
        if (args.PropertyName is nameof(AppSettings.OverlayLeft) or nameof(AppSettings.OverlayTop)
            or nameof(AppSettings.OverlayDockedToTaskbar))
            TrySave(notify: false);
        else
            _timer.Start();
    }

    private void OnTimer(object? sender, EventArgs args)
    {
        _timer.Stop();
        TrySave(notify: true);
    }

    private void TrySave(bool notify)
    {
        try
        {
            _store.SaveNow(_settings);
            ResetRetry();
            if (notify) _afterSave();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Settings save will be retried: {0}", exception);
            if (_disposed) return;
            _retryCount++;
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Min(30_000,
                Debounce.TotalMilliseconds * Math.Pow(2, Math.Min(_retryCount - 1, 8))));
            _timer.Start();
        }
    }

    private void ResetRetry()
    {
        _retryCount = 0;
        _timer.Interval = Debounce;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.PropertyChanged -= OnSettingsChanged;
        _timer.Stop();
        _timer.Tick -= OnTimer;
    }
}
