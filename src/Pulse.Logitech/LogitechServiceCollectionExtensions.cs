using Pulse.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.Logitech;

public static class LogitechServiceCollectionExtensions
{
    /// <summary>Registers the Logitech HID++ <c>IBatteryProvider</c> (singleton; the container disposes its HID handles).</summary>
    public static IServiceCollection AddLogitechHidpp(this IServiceCollection services)
    {
        services.AddSingleton<IBatteryProvider, LogitechHidppBatteryProvider>();
        return services;
    }
}
