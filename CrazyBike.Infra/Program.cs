using System.Threading.Tasks;
using Pulumi;

namespace CrazyBike.Infra
{
    internal static class Program
    {
        static Task<int> Main() => Deployment.RunAsync<CrazyBikeStack>();
    }
}
