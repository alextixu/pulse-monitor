using Pulse.Core.Abstractions;
using Pulse.Core.Localization;
using Pulse.Core.Models;

namespace Pulse.Engine.Memory;

/// <summary>
/// Decides when a low-battery alert is due (same rule the old popover used): notifications on, device connected and
/// visible, level at or below the threshold and not charging / full. Each device is alerted once; it becomes eligible
/// again after it charged (Charging / Full) or rose <see cref="ResetMarginPercent"/> % above the threshold. The set of
/// alerted devices is persisted so a restart does not repeat the alert. Not thread-safe: owned by the engine thread.
/// </summary>
internal sealed class LowBatteryWatcher
{
    public const int ResetMarginPercent = 5;

    private readonly HashSet<string> _notified;

    public LowBatteryWatcher(IEnumerable<string>? notified)
        => _notified = new HashSet<string>(notified?.Where(id => !string.IsNullOrEmpty(id)) ?? Array.Empty<string>(), StringComparer.Ordinal);

    public IReadOnlyCollection<string> Notified => _notified;

    /// <summary>
    /// Evaluates one battery list (every device, hidden ones included). Returns the alerts to show now;
    /// <paramref name="changed"/> is true when the persisted set changed.
    /// </summary>
    public IReadOnlyList<LowBatteryToast> Evaluate(IReadOnlyList<BatteryDevice> devices, AppSettings settings, out bool changed)
    {
        changed = false;
        var threshold = settings.LowBatteryThresholdPercent;
        var hidden = settings.HiddenDeviceIds;
        List<LowBatteryToast>? toasts = null;

        foreach (var device in devices)
        {
            // Only fresh information resets: a remembered (disconnected) level says nothing about charging.
            if (device.IsConnected && _notified.Contains(device.Id)
                && (device.Status is BatteryStatus.Charging or BatteryStatus.Full || device.Percent is { } p && p >= threshold + ResetMarginPercent))
            {
                _notified.Remove(device.Id);
                changed = true;
            }
        }

        if (settings.NotifyOnLowBattery)
        {
            foreach (var device in devices)
            {
                if (!device.IsConnected || device.Percent is not { } pct) continue;
                if (hidden.Contains(device.Id)) continue;
                if (device.Status is BatteryStatus.Charging or BatteryStatus.Full) continue;
                if (pct > threshold) continue;
                if (!_notified.Add(device.Id)) continue;

                changed = true;
                (toasts ??= new List<LowBatteryToast>()).Add(new LowBatteryToast(
                    device, Strings.LowBatteryTitle, string.Format(Strings.LowBatteryToastFormat, device.Name, pct)));
            }
        }

        // Forget alerts for devices the battery memory no longer knows (expired), so the set stays bounded.
        if (_notified.Count > 0)
        {
            var known = new HashSet<string>(devices.Select(d => d.Id), StringComparer.Ordinal);
            if (_notified.RemoveWhere(id => !known.Contains(id)) > 0) changed = true;
        }

        return (IReadOnlyList<LowBatteryToast>?)toasts ?? Array.Empty<LowBatteryToast>();
    }
}
