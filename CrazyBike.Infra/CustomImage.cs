using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Pulumi;
using Pulumi.Command.Local;

namespace CrazyBike.Infra
{
    public class CustomImage: ComponentResource
    {
        public Output<string> ImageName { get; }
        
        const string RootAlphaCustomImageTypeName = "alpha:CustomImage";
        
        public CustomImage(string name, CustomImageArgs args, ComponentResourceOptions options = null)
            : base(RootAlphaCustomImageTypeName, name, options)
        {
            // Normalize the context to the base of the pulumi project
            var context = Path.GetFullPath(args.Context);

            var hash = GenerateHash(context);
            
            ImageName = Output.Create($"{args.RegistryServer}/{name}:{hash}");

            var buildArgsOutputs = args.BuildArgs.Select(kvp => Output.Create($"--build-arg {kvp.Key}={kvp.Value}")).ToList();

            var buildArgs = Output.All(buildArgsOutputs).Apply(a => string.Join(" ", a));

            var dockerFile = args.Dockerfile != null
                ? args.Dockerfile.IndexOf("/", StringComparison.Ordinal) > -1 ? args.Dockerfile : $"{context}/{args.Dockerfile}"
                : $"{context}/Dockerfile";

            // Build and push locally (may have some requirements on your local environment, i.e. docker)
            // We use the bash `|| :` here because if there are concurrent builds the login command will fail since
            // we're already logged in. I couldn't find any graceful ways to make this login work
            var command = new Command($"{name}-docker-build-and-push",
                new CommandArgs
                {
                    Create = "(docker login -u $USERNAME -p $PASSWORD $ADDRESS || :) && " +
                             "docker build -t $NAME $BUILD_ARGS $CONTEXT -f $DOCKERFILE && " +
                             "docker push $NAME",
                    Environment = new InputMap<string>
                    {
                        { "NAME", ImageName },
                        { "BUILD_ARGS", buildArgs },
                        { "CONTEXT", context },
                        { "ADDRESS", args.RegistryServer },
                        { "USERNAME", args.RegistryUsername },
                        { "PASSWORD", args.RegistryPassword },
                        { "DOCKERFILE", dockerFile }
                    }
                }, new CustomResourceOptions
                {
                    Parent = this,
                    IgnoreChanges = new List<string>{"environment.USERNAME", "environment.PASSWORD"}
                });
            
            RegisterOutputs(new Dictionary<string, object>()
            {
                {"imageName", ImageName}
            });
        }
        static string GenerateHash(string context)
        {
            var allMd5Bytes = new List<byte>();
            var excludedDirectories = new[] { "bin", "obj" };
            var files = Directory.GetFiles(context, "*", SearchOption.AllDirectories);
            foreach (var fileName in files)
            {
                using var md5 = MD5.Create();
                var fileInfo = new FileInfo(fileName);
                if (excludedDirectories.Any(excludedDirectory => fileInfo.Directory != null && fileInfo.Directory.Name == excludedDirectory))
                    continue;
                
                using var stream = File.OpenRead(fileName);
                var md5Bytes = md5.ComputeHash(stream);
                
                allMd5Bytes.AddRange(md5Bytes);
            }

            using var hash = MD5.Create();
            var md5AllBytes = hash.ComputeHash(allMd5Bytes.ToArray());
            var result = BytesToHash(md5AllBytes);
            
            return result;
        }
        static string BytesToHash(IEnumerable<byte> md5Bytes) => string.Join("", md5Bytes.Select(ba => ba.ToString("x2")));
    }

    public class CustomImageArgs
    {
        public string Context { get; set; }
        public string Dockerfile { get; set; }
        public string RegistryServer { get; set; }
        public string RegistryUsername { get; set; }
        public string RegistryPassword { get; set; }
        //public ImageBuilderArgs ImageBuilderArgs { get; set; }
        public IDictionary<string, string> BuildArgs { get; set; }
    }
}