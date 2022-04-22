using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Bogus;
using CrazyBike.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrazyBike.Buy.Controllers
{
    [ApiController]
    [Route("bike")]
    public class BikeController : ControllerBase
    {
        const string AssemblerQueueName = "crazybike-assembler";
        static readonly string[] bikePartNames =
        {
            "wheel", "rim", "tire", "brake", "seat", "cassette", "rear-derailleur", "front-derailleur",  
            "chain", "chainring", "crankset", "pedal", "headset", "stem", "handlerbar", "fork", "frame",
            "hub", "bottle-cage", "disk"
        };
        static readonly string[] bikeModels = 
        { 
            "mtb-xc", "mtb-trail", "mtb-enduro", "mtb-downhill", "bdc-aero",
            "bdc-endurance", "gravel", "ciclocross", "trekking", "urban" 
        };
        
        readonly ILogger<BikeController> logger;
        readonly IConfiguration configuration;
        readonly ServiceBusAdministrationClient adminClient;
        readonly ServiceBusClient client;

        public BikeController(ILogger<BikeController> logger, IConfiguration configuration, 
            IAzureClientFactory<ServiceBusAdministrationClient> sbaFactory, IAzureClientFactory<ServiceBusClient> sbFactory)
        {
            this.logger = logger;
            this.configuration = configuration;
            adminClient = sbaFactory.CreateClient("buyAdmin");
            client = sbFactory.CreateClient("buy");;
        }

        [HttpPost("buy")]
        public async Task<IActionResult> Buy()
        {
            await CreateQueueIfNotExists(AssemblerQueueName);
            
            var bike = ProduceBike();
            var assembleBikeMessage = new AssembleBikeMessage(bike.Id, bike.Price, bike.Model, bike.Parts);
            var rawAssembleBikeMessage = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(assembleBikeMessage));
                
            var message = new ServiceBusMessage(rawAssembleBikeMessage)
            {
                MessageId = Guid.NewGuid().ToString(),
                CorrelationId = Guid.NewGuid().ToString(),
                ApplicationProperties = { {"MessageType", typeof(AssembleBikeMessage).FullName} }
            };
            
            logger.LogInformation($"Sending buying request for bike {bike.Id}");
            
            await using var sender = client.CreateSender(AssemblerQueueName);
            await sender.SendMessageAsync(message).ConfigureAwait(false);
            
            logger.LogInformation($"Bike {bike.Id} bought successfully!");

            return new AcceptedResult();
        }
        
        static Bike ProduceBike()
        {
            var bikePartGen = new Faker<BikePart>()
                .RuleFor(x => x.Id, () => Guid.NewGuid().ToString())
                .RuleFor(x => x.Name, f => f.PickRandom(bikePartNames))
                .RuleFor(x => x.Code, f => f.Commerce.Ean8());

            var bikeGen = new Faker<Bike>()
                .RuleFor(x => x.Id, () => Guid.NewGuid().ToString())
                .RuleFor(x => x.Price, f => f.Random.Number(200,10000))
                .RuleFor(x => x.Model, f => f.PickRandom(bikeModels))
                .RuleFor(u => u.Parts, f => bikePartGen.Generate(f.Random.Number(6,bikePartNames.Length)));
            
            return bikeGen.Generate();
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