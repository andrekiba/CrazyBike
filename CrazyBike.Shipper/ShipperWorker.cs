using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using CrazyBike.Shared;
using Microsoft.Extensions.Azure;

namespace CrazyBike.Shipper;
public class ShipperWorker : BackgroundService
{
    readonly IConfiguration configuration;
    readonly IHostApplicationLifetime appLifetime;
    readonly ServiceBusAdministrationClient adminClient;
    readonly ServiceBusClient client;
    readonly ServiceBusProcessor processor;
    readonly ILogger<ShipperWorker> logger;
    readonly Random random = new();
    
    const string ShipperQueueName = "crazybike-shipper";

    public ShipperWorker(IConfiguration configuration, 
        IHostApplicationLifetime appLifetime,
        IAzureClientFactory<ServiceBusAdministrationClient> sbaFactory, IAzureClientFactory<ServiceBusClient> sbFactory,
        ILogger<ShipperWorker> logger)
    {
        this.configuration = configuration;
        this.logger = logger;
        this.appLifetime = appLifetime;
        adminClient = sbaFactory.CreateClient("shipperAdmin");
        client = sbFactory.CreateClient("shipper");
        processor = client.CreateProcessor(ShipperQueueName, new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 1,
            AutoCompleteMessages = false,
            MaxAutoLockRenewalDuration = TimeSpan.FromHours(1)
        });
        processor.ProcessMessageAsync += HandleMessage;
        processor.ProcessErrorAsync += HandleError;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await CreateQueueIfNotExists(ShipperQueueName);

        await base.StartAsync(cancellationToken);
    }
    
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(async () =>
    {
        try
        {
            await processor.StartProcessingAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            await processor.StopProcessingAsync(stoppingToken);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error processing message");
        }
        finally
        {
            processor.ProcessMessageAsync -= HandleMessage;
            processor.ProcessErrorAsync -= HandleError;
            await processor.DisposeAsync();
            await client.DisposeAsync();
            appLifetime.StopApplication();
        }
    }, stoppingToken);
    
    async Task HandleMessage(ProcessMessageEventArgs args)
    {
        var message = args.Message;
        var rawMessageBody = Encoding.UTF8.GetString(message.Body);
        logger.LogInformation("Received message {MessageId} with body {MessageBody}", message.MessageId, rawMessageBody);

        var shipBikeMessage = JsonSerializer.Deserialize<ShipBikeMessage>(rawMessageBody);
        if (shipBikeMessage != null)
        {
            await Task.Delay(TimeSpan.FromSeconds(random.Next(1,5)));
            logger.LogInformation($"Bike {shipBikeMessage.Id} shipped to {shipBikeMessage.Address}!");
        }
        else
            logger.LogError("Unable to deserialize to message contract {ContractName} for message {MessageBody}", typeof(ShipBikeMessage), rawMessageBody);
        
        await args.CompleteMessageAsync(message);
        logger.LogInformation("Message {MessageId} processed", message.MessageId);
    }
    
    Task HandleError(ProcessErrorEventArgs args)
    {
        logger.LogError(args.Exception.ToString());
        return Task.CompletedTask;
    }
    
    async Task CreateQueueIfNotExists(string queueName)
    {
        if (!await adminClient.QueueExistsAsync(queueName))
        {
            var queueOptions = new CreateQueueOptions(queueName)
            {  
                //LockDuration = TimeSpan.FromMinutes(5) 
            };
            await adminClient.CreateQueueAsync(queueOptions);    
        }
    }
}