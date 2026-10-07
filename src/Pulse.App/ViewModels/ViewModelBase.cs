using CommunityToolkit.Mvvm.ComponentModel;

namespace Pulse.App.ViewModels;

/// <summary>Base for all view models (CommunityToolkit.Mvvm). Properties are expected to change on the UI thread only.</summary>
public abstract class ViewModelBase : ObservableObject
{
}
