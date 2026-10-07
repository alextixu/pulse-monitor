using Pulse.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.Asus;

public static class AsusServiceCollectionExtensions
{
    /// <summary>Registers the ASUS ROG / TUF peripheral <c>IBatteryProvider</c> (singleton; the container disposes its HID handles).</summary>
    public static IServiceCollection AddAsusRog(this IServiceCollection services)
    {
        services.AddSingleton<IBatteryProvider, AsusRogBatteryProvider>();
        return services;
    }
}
