using Avalonia.Threading;
using Pulse.Core.Abstractions;
using Pulse.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Services;

/// <summary>
/// Owns the live <see cref="AppSettings"/> instance (the same object registered in DI) and persists it through
/// <see cref="ISettingsStore"/>. Views mutate <see cref="Settings"/> directly, then call <see cref="NotifyChanged"/>:
/// that raises <see cref="Changed"/> on the UI thread (theme / tray / timers react) and schedules a debounced save.
/// </summary>
public sealed partial class SettingsService : ObservableObject, IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly ISettingsStore _store;
    private readonly ILogger<SettingsService> _log;
    private readonly DispatcherTimer _saveTimer;
    private bool _dirty;

    [ObservableProperty] private string? _lastSaveError;
    [ObservableProperty] private DateTimeOffset? _lastSavedAt;

    public SettingsService(AppSettings settings, ISettingsStore store, ILogger<SettingsService> log)
    {
        Settings = settings;
        _store = store;
        _log = log;
        _saveTimer = new DispatcherTimer(SaveDelay, DispatcherPriority.Background, (_, _) => SaveNow());
    }

    public AppSettings Settings { get; }

    public ISettingsStore Store => _store;

    public string FilePath => _store.FilePath;

    /// <summary>Raised on the UI thread after <see cref="NotifyChanged"/>. <c>e</c> is the changed property name or null for "anything".</summary>
    public event EventHandler<string?>? Changed;

    /// <summary>Call after mutating <see cref="Settings"/>: notifies listeners and schedules a debounced save.</summary>
    public void NotifyChanged(string? propertyName = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => NotifyChanged(propertyName));
            return;
        }

        Changed?.Invoke(this, propertyName);
        Save();
    }

    /// <summary>Debounced save (500 ms).</summary>
    public void Save()
    {
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes immediately (also used on shutdown).</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        _dirty = false;
        try
        {
            _store.Save(Settings);
            LastSaveError = null;
            LastSavedAt = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Saving settings to {Path} failed", _store.FilePath);
            LastSaveError = ex.Message;
        }
    }

    public void Dispose()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            SaveNow();
        }
        else
        {
            try { Dispatcher.UIThread.Invoke(SaveNow); }
            catch (Exception ex) { _log.LogDebug(ex, "Settings flush on dispose skipped"); }
        }
    }
}
