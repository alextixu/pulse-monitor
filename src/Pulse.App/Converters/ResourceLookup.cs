using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Pulse.App.Converters;

/// <summary>Resolves application resources (brushes / icon geometries) for converters, honouring the active theme variant.</summary>
internal static class ResourceLookup
{
    public static T? Find<T>(string key) where T : class
    {
        if (Application.Current is not { } app) return null;
        return app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is T typed ? typed : null;
    }

    public static IBrush Brush(string key, Color fallback) => Find<IBrush>(key) ?? new SolidColorBrush(fallback);

    public static Geometry? Icon(string key) => Find<Geometry>(key);
}
