using Pulse.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.Hardware;

public static class HardwareServiceCollectionExtensions
{
    /// <summary>Registers the LibreHardwareMonitor based <c>IHardwareMonitor</c> singleton.</summary>
    public static IServiceCollection AddLibreHardwareMonitor(this IServiceCollection services)
    {
        services.AddSingleton<LibreHardwareMonitorService>();
        services.AddSingleton<IHardwareMonitor>(sp => sp.GetRequiredService<LibreHardwareMonitorService>());
        return services;
    }
}
