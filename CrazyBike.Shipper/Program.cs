using CrazyBike.Shipper;
using Microsoft.Extensions.Azure;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.secret.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

var host = Host.CreateDefaultBuilder(args)
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
            builder.AddServiceBusAdministrationClient(configuration["ASBConnectionString"])
                .WithName("shipperAdmin");
            builder.AddServiceBusClient(configuration["ASBConnectionString"])
                .WithName("shipper")
                .ConfigureOptions(options =>
                {
                    options.EnableCrossEntityTransactions = true;
                });
        });
        services.AddHostedService<ShipperWorker>();
    })
    .Build();

await host.RunAsync();