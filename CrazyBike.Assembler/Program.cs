using Microsoft.Extensions.Azure;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace CrazyBike.Assembler
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            var host = CreateHostBuilder(args).Build();
            await host.RunAsync();
        }
        
        static IHostBuilder CreateHostBuilder(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile("appsettings.secret.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();
            
            return Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((hostingContext, config) => config.AddConfiguration(configuration))
                .ConfigureLogging((hostBuilderContext, loggingBuilder) =>
                {
                    var logger = new LoggerConfiguration()
                        .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}{NewLine}", theme: AnsiConsoleTheme.Code)
                        .MinimumLevel.Error()
                        .CreateLogger();

                    loggingBuilder.AddSerilog(logger);
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddAzureClients(builder =>
                    {
                        builder.AddServiceBusClient(configuration["ASB:ConnectionString"]);
                    });
                    services.AddHostedService<AssemblerWorker>();
                });
        }    
    }
}