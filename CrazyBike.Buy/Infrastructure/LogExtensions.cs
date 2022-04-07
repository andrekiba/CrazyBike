using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace CrazyBike.Buy.Infrastructure;

public static class LogExtensions
{
    public static void ConfigureLogger(this IServiceCollection services, IConfiguration config)
    {
        var logger = new LoggerConfiguration()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}{NewLine}", theme: AnsiConsoleTheme.Code)
            .MinimumLevel.Error()
            .CreateLogger();

        services.AddLogging(lb => lb.AddSerilog(logger));
    }
}