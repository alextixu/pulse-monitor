using Pulse.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Pulse.Rgb;

public static class RgbServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OpenRGB based <c>IRgbController</c> singleton (<see cref="OpenRgbController"/>).
    /// Requires logging (<c>ILogger&lt;T&gt;</c>) to be registered. The controller starts disconnected; call
    /// <c>Configure(host, port)</c> and <c>ConnectAsync</c> from the app.
    /// </summary>
    public static IServiceCollection AddOpenRgb(this IServiceCollection services)
    {
        services.TryAddSingleton<OpenRgbController>();
        services.TryAddSingleton<IRgbController>(sp => sp.GetRequiredService<OpenRgbController>());
        return services;
    }
}
