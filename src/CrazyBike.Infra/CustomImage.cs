using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Pulumi;
using Pulumi.Command.Local;

namespace CrazyBike.Infra
{
    public class CustomImage: ComponentResource
    {
        [Output] public Output<string> Name { get; set; }
        //[Output] public Output<string> StdOut {get; set;}
        //[Output] public Output<string> StdErr {get; set;}
        
        const string RootAlphaCustomImageTypeName = "alpha:CustomImage";
        
        public CustomImage(string name, CustomImageArgs args, ComponentResourceOptions options = null)
            : base(RootAlphaCustomImageTypeName, name, options)
        {
            // Normalize the context to the base of the pulumi project
            //var context = Path.GetFullPath(args.Context);
            var context = args.Context;
            
            var hash = args.BuildId + "1";
            
            Name = Output.Format($"{args.RegistryArgs.Server}/{name}:latest-{hash}");

            var buildArgsOutputs = args.BuildArgs.Select(kvp => Output.Format($"--build-arg {kvp.Key}={kvp.Value}")).ToList();

            var buildArgs = Output.All(buildArgsOutputs).Apply(a => string.Join(" ", a));

            var dockerFile = !string.IsNullOrEmpty(args.Dockerfile)
                ? args.Dockerfile.IndexOf("/", StringComparison.Ordinal) > -1 ? args.Dockerfile : Path.Combine(context, args.Dockerfile)
                : Path.Combine(context, "Dockerfile");

            // Build and push locally (may have some requirements on your local environment, i.e. docker)
            // We use the bash `|| :` here because if there are concurrent builds the login command will fail since
            // we're already logged in. I couldn't find any graceful ways to make this login work
            
            var command = new Command($"{name}-docker-build-and-push",
                new CommandArgs
                {
                    //Dir = Directory.GetCurrentDirectory(),
                    //Create = Output.Format($"(docker login -u $USERNAME -p $PASSWORD $SERVER || :) && docker build -f $DOCKERFILE -t $NAME $CONTEXT && docker image push $NAME"),
                    //Create = Output.Format(@$"dotnet list"),
                    //Create = Output.Format(@$"docker login -u {args.RegistryArgs.Username} -p {args.RegistryArgs.Password} {args.RegistryArgs.Server}"),
                    //Create = Output.Format($"(docker login -u {args.RegistryArgs.Username} -p {args.RegistryArgs.Password} {args.RegistryArgs.Server} || :) && docker build -f {dockerFile} -t {Name} {context} && docker image push {Name}"),
                    Create = "./dockerBuildPush.sh",
                    Environment = new InputMap<string>
                    {
                        { "NAME", Name },
                        { "BUILD_ARGS", buildArgs },
                        { "CONTEXT", context },
                        { "SERVER", args.RegistryArgs.Server },
                        { "USERNAME", args.RegistryArgs.Username },
                        { "PASSWORD", args.RegistryArgs.Password },
                        { "DOCKERFILE", dockerFile }
                    },
                    Triggers = new []
                    {
                        hash
                    }
                }, new CustomResourceOptions
                {
                    Parent = this,
                    IgnoreChanges = new List<string>{"environment.USERNAME", "environment.PASSWORD"},
                    DeleteBeforeReplace = true
                });
            
            //StdOut = command.Stdout;
            //StdErr = command.Stderr;
        }
    }

    public class CustomImageArgs
    {
        public string Context { get; set; }
        public string Dockerfile { get; set; }
        public string BuildId { get; set; }
        public CustomRegistryArgs RegistryArgs { get; set; }
        public IDictionary<string, Output<string>> BuildArgs { get; set; } = new Dictionary<string, Output<string>>();
    }

    public class CustomRegistryArgs
    {
        public Input<string> Server { get; set; }
        public Input<string> Username { get; set; }
        public Input<string> Password { get; set; }
    }
}