using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Bogus;
using CrazyBike.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CrazyBike.Buy.Controllers
{
    [ApiController]
    [Route("bike")]
    public class BikeController : ControllerBase
    {
        const string AssemblerQueueName = "fantastic-bike-assembler";
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

        public BikeController(ILogger<BikeController> logger, IConfiguration configuration)
        {
            this.logger = logger;
            this.configuration = configuration;
        }

        [HttpPost("buy")]
        public async Task<IActionResult> Buy()
        {
            var bike = ProduceBike();
            var bikeMessage = new AssembleBikeMessage(bike.Id, bike.Price, bike.Model, bike.Parts);
            var rowBikeMessage = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(bikeMessage));
                
            var message = new ServiceBusMessage(rowBikeMessage)
            {
                MessageId = Guid.NewGuid().ToString()
            };
            
            logger.LogWarning($"Sending buying request for bike {bike.Id}");
            
            var client = new ServiceBusClient(configuration["ASBConnectionString"]);
            var sender = client.CreateSender(AssemblerQueueName);
            await sender.SendMessageAsync(message).ConfigureAwait(false);
            
            logger.LogWarning($"Bike {bike.Id} bought successfully!");
            
            await sender.DisposeAsync();
            await client.DisposeAsync();
            
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
    }
}