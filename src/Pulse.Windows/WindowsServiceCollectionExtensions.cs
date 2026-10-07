using Pulse.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.Windows;

public static class WindowsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Windows battery providers (<c>IBatteryProvider</c>: Bluetooth + system battery),
    /// <c>IElevationService</c> and <c>IAutoStartService</c>. All singletons; requires <c>ILogger&lt;T&gt;</c> to be registered.
    /// </summary>
    public static IServiceCollection AddWindowsPlatformServices(this IServiceCollection services)
    {
        services.AddSingleton<IBatteryProvider, WindowsBluetoothBatteryProvider>();
        services.AddSingleton<IBatteryProvider, WindowsSystemBatteryProvider>();
        services.AddSingleton<IElevationService, WindowsElevationService>();
        services.AddSingleton<IAutoStartService, WindowsAutoStartService>();
        return services;
    }
}
