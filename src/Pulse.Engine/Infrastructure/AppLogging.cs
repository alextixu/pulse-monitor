using Microsoft.Extensions.Logging;

namespace Pulse.Engine;

/// <summary>Shared logging configuration: file sink, Information level, Debug for our own namespaces in DEBUG builds.</summary>
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
    }

    /// <summary>A logger factory writing to <paramref name="fileProvider"/> only.</summary>
    public static ILoggerFactory CreateFactory(FileLoggerProvider fileProvider) => LoggerFactory.Create(b => Configure(b, fileProvider));
}
