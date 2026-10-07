using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Pulse.App.Services;
using Pulse.App.ViewModels;
using Microsoft.Extensions.Logging;

namespace Pulse.App.Views;

/// <summary>
/// Frameless tray popover. The visible card is 400×660 DIPs (height shrinks to fit the work area); the window is
/// larger by <see cref="ShadowMargin"/> on each side so the drop shadow is not clipped. Hidden (never closed) on
/// Escape / deactivation; <see cref="ShowPopover"/> repositions next to the tray every time.
/// </summary>
public partial class PopoverWindow : Window
{
    public const double CardWidth = 400;
    public const double CardHeight = 660;
    public const double ShadowMargin = 24;

    /// <summary>A deactivate-hide followed by a tray click within this window is treated as "toggle closed".</summary>
    private static readonly TimeSpan ToggleGuard = TimeSpan.FromMilliseconds(350);

    private readonly MainViewModel _viewModel;
    private readonly ILogger? _log = App.LoggerFactory?.CreateLogger<PopoverWindow>();
    private readonly Queue<LowBatteryEventArgs> _pendingToasts = new();
    private WindowNotificationManager? _notifications;

    /// <summary>Runtime-loader / designer constructor; the app uses <see cref="PopoverWindow(MainViewModel)"/>.</summary>
    public PopoverWindow()
        : this(App.Current?.MainViewModel ?? throw new InvalidOperationException("PopoverWindow needs a MainViewModel."))
    {
    }

    public PopoverWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        Deactivated += OnDeactivated;
        Opened += (_, _) => _viewModel.Monitoring.NotifyPopoverOpened();
        _viewModel.LowBatteryDetected += OnLowBatteryDetected;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Inline toasts (low battery) live in the window's adorner layer, inside the card (shadow margin + inset).
        _notifications ??= new WindowNotificationManager(this)
        {
            Position = NotificationPosition.BottomCenter,
            MaxItems = 2,
            Margin = new Thickness(ShadowMargin + 12, ShadowMargin + 12, ShadowMargin + 12, ShadowMargin + 16),
        };
        // Background priority: the manager's template must be applied before Show() can add cards.
        Dispatcher.UIThread.Post(FlushToasts, DispatcherPriority.Background);
    }

    /// <summary>Dismisses visible toasts (screenshot mode captures them on the first tab only).</summary>
    public void CloseToasts() => _notifications?.CloseAll();

    private void OnLowBatteryDetected(object? sender, LowBatteryEventArgs e)
    {
        _pendingToasts.Enqueue(e);
        if (IsVisible) Dispatcher.UIThread.Post(FlushToasts, DispatcherPriority.Background);
    }

    private void FlushToasts()
    {
        if (_notifications is null || !IsVisible) return;
        while (_pendingToasts.Count > 0)
        {
            var toast = _pendingToasts.Dequeue();
            try
            {
                _notifications.Show(new Notification(toast.Title, toast.Message, NotificationType.Warning, TimeSpan.FromSeconds(8)));
            }
            catch (Exception ex)
            {
                _log?.LogDebug(ex, "Toast failed");
            }
        }
    }

    /// <summary>False in screenshot mode so the capture is not interrupted.</summary>
    public bool HideOnDeactivate { get; set; } = true;

    public DateTimeOffset LastHiddenAt { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>Visual rendered by the screenshot service (window content incl. shadow margin).</summary>
    public Control ScreenshotRoot => Root;

    /// <summary>The active tab's content (full extent, not clipped by the scroll viewer) for the "-full" screenshots.</summary>
    public Control TabContentRoot => TabContent;

    /// <summary>Paints the transparent margin with a neutral backdrop so screenshots show the shadow.</summary>
    public void SetScreenshotBackdrop(bool enabled)
    {
        Root.Background = enabled ? new SolidColorBrush(Color.Parse("#B8BCC8")) : Brushes.Transparent;
        if (enabled) TabContent[!TemplatedControl.BackgroundProperty] = this.GetResourceObservable("BccBackgroundBrush").ToBinding();
        else TabContent.ClearValue(TemplatedControl.BackgroundProperty);
    }

    public void ShowPopover()
    {
        Reposition();
        if (!IsVisible)
        {
            Show();
        }
        Activate();
        _viewModel.IsPopoverVisible = true;
        _viewModel.Monitoring.NotifyPopoverOpened();
        Dispatcher.UIThread.Post(FlushToasts, DispatcherPriority.Background);
    }

    public void HidePopover()
    {
        if (!IsVisible) return;
        Hide();
        LastHiddenAt = DateTimeOffset.Now;
        _viewModel.IsPopoverVisible = false;
    }

    /// <summary>Tray-click behaviour: hide when visible, otherwise show (unless it was just hidden by the same click).</summary>
    public void TogglePopover()
    {
        if (IsVisible)
        {
            HidePopover();
            return;
        }

        if (DateTimeOffset.Now - LastHiddenAt < ToggleGuard) return;
        ShowPopover();
    }

    private void Reposition()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;

        var placement = PopoverPlacement.Compute(screen, new Size(CardWidth, CardHeight));
        var cardHeight = Math.Min(CardHeight, placement.MaxHeightDips);
        if (cardHeight < CardHeight)
        {
            placement = PopoverPlacement.Compute(screen, new Size(CardWidth, cardHeight));
        }

        Card.Width = CardWidth;
        Card.Height = cardHeight;
        Width = CardWidth + 2 * ShadowMargin;
        Height = cardHeight + 2 * ShadowMargin;

        var offset = (int)Math.Round(ShadowMargin * screen.Scaling);
        Position = new PixelPoint(placement.Position.X - offset, placement.Position.Y - offset);
        _log?.LogInformation("Popover placed at card ({X},{Y}) px, {W}x{H} DIP, work area {Area}, scaling {Scaling}",
            placement.Position.X, placement.Position.Y, CardWidth, cardHeight, screen.WorkingArea, screen.Scaling);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (HideOnDeactivate) HidePopover();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HidePopover();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The popover is never closed by the user; App shuts the lifetime down instead.
        if (!e.IsProgrammatic)
        {
            e.Cancel = true;
            HidePopover();
        }
        base.OnClosing(e);
    }
}
