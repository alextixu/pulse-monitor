using System.Windows.Input;
using Pulse.App.Services;

namespace Pulse.App.ViewModels;

/// <summary>
/// Base of the five tab view models. Holds the shared <see cref="AgentConnection"/> (agent state, settings copy and
/// commands). <see cref="OnActivated"/> / <see cref="OnDeactivated"/> are called by <see cref="MainViewModel"/> on tab switches.
/// </summary>
public abstract class TabViewModelBase : ViewModelBase
{
    protected TabViewModelBase(MainTab tab, AgentConnection agent)
    {
        Tab = tab;
        Agent = agent;
        Title = MainTabItem.TitleOf(tab);
        IconKey = MainTabItem.IconKeyOf(tab);
    }

    public MainTab Tab { get; }

    /// <summary>Localized tab title (Strings.TabXxx).</summary>
    public string Title { get; }

    /// <summary>Icon resource key (Styles/Icons.axaml).</summary>
    public string IconKey { get; }

    /// <summary>The agent as seen from the UI thread.</summary>
    public AgentConnection Agent { get; }

    /// <summary>"Run as administrator" (MainViewModel.ElevateCommand); assigned by <see cref="MainViewModel"/> so banners can reuse it.</summary>
    public ICommand? ElevateCommand { get; internal set; }

    public bool IsActive { get; private set; }

    internal void Activate()
    {
        if (IsActive) return;
        IsActive = true;
        OnActivated();
    }

    internal void Deactivate()
    {
        if (!IsActive) return;
        IsActive = false;
        OnDeactivated();
    }

    /// <summary>
    /// Sends any change still waiting in a debounce timer (slider drags) and returns when it was sent; called before the
    /// popover process exits.
    /// </summary>
    public virtual Task FlushPendingAsync() => Task.CompletedTask;

    /// <summary>Called on the UI thread when the tab becomes visible.</summary>
    protected virtual void OnActivated() { }

    /// <summary>Called on the UI thread when another tab is selected.</summary>
    protected virtual void OnDeactivated() { }
}
