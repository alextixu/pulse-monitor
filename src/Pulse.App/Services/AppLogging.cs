using Microsoft.Extensions.Logging;

namespace Pulse.App.Services;

/// <summary>Shared logging configuration: file sink (+ Debug sink), Information level, Debug for our own namespaces in DEBUG builds.</summary>
public static class AppLogging
{
    public static void Configure(ILoggingBuilder builder, FileLoggerProvider fileProvider)
    {
        builder.ClearProviders();
        builder.SetMinimumLevel(LogLevel.Information);
#if DEBUG
        builder.AddFilter("Pulse", LogLevel.Debug);
#endif
        builder.AddFilter("Microsoft", LogLevel.Warning);
        builder.AddFilter("System", LogLevel.Warning);
        builder.AddProvider(fileProvider);
        builder.AddDebug();
    }
}
