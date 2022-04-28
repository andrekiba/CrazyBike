using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Bogus;
using CrazyBike.Shared;
using Microsoft.Extensions.Azure;

namespace CrazyBike.Assembler
{
    public class AssemblerWorker : BackgroundService
    {
        readonly IConfiguration configuration;
        readonly IHostApplicationLifetime appLifetime;
        readonly ServiceBusAdministrationClient adminClient;
        readonly ServiceBusClient client;
        readonly ServiceBusProcessor processor;
        readonly ServiceBusSender sender;
        readonly ILogger<AssemblerWorker> logger;
        readonly Faker faker = new();
        
        const string AssemblerQueueName = "crazybike-assembler";
        const string ShipperQueueName = "crazybike-shipper";
        
        public AssemblerWorker(IConfiguration configuration, 
            IHostApplicationLifetime appLifetime,
            IAzureClientFactory<ServiceBusAdministrationClient> sbaFactory, IAzureClientFactory<ServiceBusClient> sbFactory,
            ILogger<AssemblerWorker> logger)
        {
            this.configuration = configuration;
            this.logger = logger;
            this.appLifetime = appLifetime;
            adminClient = sbaFactory.CreateClient("assemblerAdmin");
            client = sbFactory.CreateClient("assembler");
            processor = client.CreateProcessor(AssemblerQueueName, new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls = 1,
                AutoCompleteMessages = false,
                MaxAutoLockRenewalDuration = TimeSpan.FromHours(1)
            });
            processor.ProcessMessageAsync += HandleMessage;
            processor.ProcessErrorAsync += HandleError;
            sender = client.CreateSender(ShipperQueueName);
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            await CreateQueueIfNotExists(AssemblerQueueName);
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
                await sender.DisposeAsync();
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

            var assembleBikeMessage = JsonSerializer.Deserialize<AssembleBikeMessage>(rawMessageBody);
            if (assembleBikeMessage != null)
            {
                await Task.Delay(TimeSpan.Parse(configuration.GetValue<string>("FakeWorkDuration")));

                var shipBikeMessage = new ShipBikeMessage(assembleBikeMessage.Id, faker.Address.FullAddress());
                var rowShipBikeMessage = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(shipBikeMessage));
                
                var outMessage = new ServiceBusMessage(rowShipBikeMessage)
                {
                    MessageId = Guid.NewGuid().ToString(),
                    CorrelationId = args.Message.CorrelationId,
                    ApplicationProperties = { {"MessageType", typeof(ShipBikeMessage).FullName} }
                };
                
                await sender.SendMessageAsync(outMessage).ConfigureAwait(false);
            
                logger.LogWarning($"Bike {assembleBikeMessage.Id} assembled and ready to be shipped!");
            }
            else
                logger.LogError("Unable to deserialize to message contract {ContractName} for message {MessageBody}", typeof(AssembleBikeMessage), rawMessageBody);
            
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
}

