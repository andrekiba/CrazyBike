using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Pulumi;
using Pulumi.AzureNative.App.Inputs;
using Pulumi.AzureNative.ContainerRegistry;
using Pulumi.AzureNative.ContainerRegistry.Inputs;
using Pulumi.AzureNative.OperationalInsights;
using Pulumi.AzureNative.OperationalInsights.Inputs;
using Pulumi.AzureNative.Resources;
using Pulumi.Command.Local;
using Deployment = Pulumi.Deployment;
using ASB = Pulumi.AzureNative.ServiceBus;
using App = Pulumi.AzureNative.App;
using ACR = Pulumi.AzureNative.ContainerRegistry;
using Resource = Pulumi.Resource;

namespace CrazyBike.Infra
{
    internal class CrazyBikeStack : Stack
    {
        #region Output
        [Output] public Output<string> ASBPrimaryConnectionString { get; set; }
        [Output] public Output<string> BuyUrl { get; set; }
        [Output] public Output<string> BuyImageTag { get; set; }
        [Output] public Output<string> AssemblerImageTag { get; set; }
        [Output] public Output<string> ShipperImageTag { get; set; }
        [Output] public Output<string> BuyBuildOutput {get; set;}
        [Output] public Output<string> AssemblerBuildOutput {get; set;}
        [Output] public Output<string> ShipperBuildOutput {get; set;}

        #endregion
        
        public CrazyBikeStack()
        {
            /*
            while (!Debugger.IsAttached)
            {
                Thread.Sleep(100);
            }
            */
            
            const string projectName = "crazybike";
            var stackName = Deployment.Instance.StackName;
            
            #region Resource Group
            
            var resourceGroupName = $"{projectName}-{stackName}-rg";
            var resourceGroup = new ResourceGroup(resourceGroupName, new ResourceGroupArgs
            {
                ResourceGroupName = resourceGroupName
            });

            #endregion
            
            #region ASB
            
            var asbNamespaceName = $"{projectName}{stackName}ns";
            var asbNamespace = new ASB.Namespace(asbNamespaceName, new ASB.NamespaceArgs
            {
                NamespaceName = asbNamespaceName,
                ResourceGroupName = resourceGroup.Name,
                Sku = new ASB.Inputs.SBSkuArgs
                {
                    Name = ASB.SkuName.Standard,
                    Tier = ASB.SkuTier.Standard
                }
            });
            
            ASBPrimaryConnectionString = Output.Tuple(resourceGroup.Name, asbNamespace.Name).Apply(names =>
                Output.Create(GetASBPrimaryConectionString(names.Item1, names.Item2)));
            
            #endregion
            
            #region Log Analytics
            
            var logWorkspaceName = $"{projectName}-{stackName}-log";
            var logWorkspace = new Workspace(logWorkspaceName, new WorkspaceArgs
            {
                WorkspaceName = logWorkspaceName,
                ResourceGroupName = resourceGroup.Name,
                Sku = new WorkspaceSkuArgs { Name = "PerGB2018" },
                RetentionInDays = 30
            });
        
            var logWorkspaceSharedKeys = Output.Tuple(resourceGroup.Name, logWorkspace.Name).Apply(items =>
                GetSharedKeys.InvokeAsync(new GetSharedKeysArgs
                {
                    ResourceGroupName = items.Item1,
                    WorkspaceName = items.Item2,
                }));
            
            #endregion
            
            #region Container Registry
            
            var containerRegistryName = $"{projectName}{stackName}cr";
            var containerRegistry = new Registry(containerRegistryName, new RegistryArgs
            {
                RegistryName = containerRegistryName,
                ResourceGroupName = resourceGroup.Name,
                Sku = new SkuArgs { Name = "Basic" },
                AdminUserEnabled = true
            });
            
            var credentials = Output.Tuple(resourceGroup.Name, containerRegistry.Name).Apply(items =>
                ListRegistryCredentials.InvokeAsync(new ListRegistryCredentialsArgs
                {
                    ResourceGroupName = items.Item1,
                    RegistryName = items.Item2
                }));
            var adminUsername = credentials.Apply(c => c.Username);
            var adminPassword = credentials.Apply(c => c.Passwords[0].Value);
            
            #endregion
            
            #region ACR commands
            
            const string azAcrBuildAndPush = "az acr build -r $REGISTRY -t $IMAGENAME -f $DOCKERFILE $CONTEXT";
            var buildContext = Path.GetFullPath(Directory.GetParent(Directory.GetCurrentDirectory()).FullName);
            
            var buyContext = Path.Combine(buildContext,"CrazyBike.Buy");
            var buyImageName = $"{projectName}-buy";
            var buyContextHash = buyContext.GenerateHash();
            BuyImageTag = Output.Format($"{containerRegistry.LoginServer}/{buyImageName}:latest-{buyContextHash}");
            var buyBuildPushCommand = new Command("buy-build-and-push",
                new CommandArgs
                {
                    Dir = Directory.GetCurrentDirectory(),
                    Create = azAcrBuildAndPush,
                    Environment = new InputMap<string>
                    {
                        { "IMAGENAME", BuyImageTag },
                        { "CONTEXT", buildContext },
                        { "REGISTRY", containerRegistry.Name },
                        { "DOCKERFILE", Path.Combine(buildContext, "Dockerfile.buy") }
                    },
                    Triggers = new []
                    {
                        BuyImageTag
                    }
                },
                new CustomResourceOptions
                {
                    Parent = this,
                    DeleteBeforeReplace = true
                });
            BuyBuildOutput = buyBuildPushCommand.Stdout;
            
            var assemblerContext = Path.Combine(buildContext,"CrazyBike.Assembler");
            var assemblerImageName = $"{projectName}-assembler";
            var assemblerContextHash = assemblerContext.GenerateHash();
            AssemblerImageTag = Output.Format($"{containerRegistry.LoginServer}/{assemblerImageName}:latest-{assemblerContextHash}");
            var assemblerBuildPushCommand = new Command("assembler-build-and-push",
                new CommandArgs
                {
                    Dir = Directory.GetCurrentDirectory(),
                    Create = azAcrBuildAndPush,
                    Environment = new InputMap<string>
                    {
                        { "IMAGENAME", AssemblerImageTag },
                        { "CONTEXT", buildContext },
                        { "REGISTRY", containerRegistry.Name },
                        { "DOCKERFILE", Path.Combine(buildContext, "Dockerfile.assembler") }
                    },
                    Triggers = new []
                    {
                        AssemblerImageTag
                    }
                }, 
                new CustomResourceOptions
                {
                    Parent = this,
                    DeleteBeforeReplace = true
                });
            AssemblerBuildOutput = assemblerBuildPushCommand.Stdout;
            
            var shipperContext = Path.Combine(buildContext,"CrazyBike.Shipper");
            var shipperImageName = $"{projectName}-shipper";
            var shipperContextHash = shipperContext.GenerateHash();
            ShipperImageTag = Output.Format($"{containerRegistry.LoginServer}/{shipperImageName}:latest-{shipperContextHash}");
            var shipperBuildPushCommand = new Command("shipper-build-and-push",
                new CommandArgs
                {
                    Dir = Directory.GetCurrentDirectory(),
                    Create = azAcrBuildAndPush,
                    Environment = new InputMap<string>
                    {
                        { "IMAGENAME", ShipperImageTag },
                        { "CONTEXT", buildContext },
                        { "REGISTRY", containerRegistry.Name },
                        { "DOCKERFILE", Path.Combine(buildContext, "Dockerfile.shipper") }
                    },
                    Triggers = new []
                    {
                        ShipperImageTag
                    }
                }, 
                new CustomResourceOptions
                {
                    Parent = this,
                    DeleteBeforeReplace = true
                });
            ShipperBuildOutput = shipperBuildPushCommand.Stdout;
            
            #endregion

            #region Container Apps

            const string asbConnectionSecret = "asb-connection";

            var kubeEnvName = $"{projectName}-{stackName}-env";
            var kubeEnv = new App.ManagedEnvironment(kubeEnvName, new App.ManagedEnvironmentArgs
            {
                Name = kubeEnvName,
                ResourceGroupName = resourceGroup.Name,
                AppLogsConfiguration = new AppLogsConfigurationArgs
                {
                    Destination = "log-analytics",
                    LogAnalyticsConfiguration = new LogAnalyticsConfigurationArgs
                    {
                        CustomerId = logWorkspace.CustomerId,
                        SharedKey = logWorkspaceSharedKeys.Apply(r => r.PrimarySharedKey)
                    }
                }
            });
            
            var buyName = $"{projectName}-{stackName}-ca-buy";
            var buy = new App.ContainerApp(buyName, new App.ContainerAppArgs
            {
                Name = buyName,
                ResourceGroupName = resourceGroup.Name,
                ManagedEnvironmentId = kubeEnv.Id,
                Configuration = new ConfigurationArgs
                {
                    Ingress = new IngressArgs
                    {
                        External = true,
                        TargetPort = 80
                    },
                    Registries =
                    {
                        new RegistryCredentialsArgs
                        {
                            Server = containerRegistry.LoginServer,
                            Username = adminUsername,
                            PasswordSecretRef = $"{containerRegistryName}-admin-pwd"
                        }
                    },
                    Secrets = 
                    {
                        new App.Inputs.SecretArgs
                        {
                            Name = $"{containerRegistryName}-admin-pwd",
                            Value = adminPassword
                        },
                        new App.Inputs.SecretArgs
                        {
                            Name = asbConnectionSecret,
                            Value = ASBPrimaryConnectionString
                        }
                    }
                },
                Template = new TemplateArgs
                {
                    Containers = 
                    {
                        new App.Inputs.ContainerArgs
                        {
                            Name = buyImageName,
                            Image = BuyImageTag,
                            Env = new[]
                            {
                                new EnvironmentVarArgs
                                {
                                    Name = "ASBConnectionString",
                                    SecretRef = asbConnectionSecret
                                }
                            }
                        }
                    },
                    Scale = new ScaleArgs
                    {
                        MinReplicas = 0,
                        MaxReplicas = 5,
                        Rules = new List<ScaleRuleArgs>
                        {
                            new ScaleRuleArgs
                            {
                                Name = "buy-http-requests",
                                Http = new HttpScaleRuleArgs
                                {
                                    Metadata =
                                    {
                                        {"concurrentRequests", "50"}
                                    }
                                }
                            }
                        }
                    }
                }
            }, new CustomResourceOptions
            {
                IgnoreChanges = new List<string> {"tags"},
                DependsOn = new InputList<Resource>{ buyBuildPushCommand }
            });
            BuyUrl = Output.Format($"https://{buy.Configuration.Apply(c => c.Ingress).Apply(i => i.Fqdn)}");
            
            var assemblerName = $"{projectName}-{stackName}-ca-assembler";
            var assembler = new App.ContainerApp(assemblerName, new App.ContainerAppArgs
            {
                Name = assemblerName,
                ResourceGroupName = resourceGroup.Name,
                ManagedEnvironmentId = kubeEnv.Id,
                Configuration = new ConfigurationArgs
                {
                    Registries =
                    {
                        new RegistryCredentialsArgs
                        {
                            Server = containerRegistry.LoginServer,
                            Username = adminUsername,
                            PasswordSecretRef = $"{containerRegistryName}-admin-pwd"
                        }
                    },
                    Secrets = 
                    {
                        new App.Inputs.SecretArgs
                        {
                            Name = $"{containerRegistryName}-admin-pwd",
                            Value = adminPassword
                        },
                        new App.Inputs.SecretArgs
                        {
                            Name = asbConnectionSecret,
                            Value = ASBPrimaryConnectionString
                        }
                    }
                },
                Template = new TemplateArgs
                {
                    Containers = 
                    {
                        new App.Inputs.ContainerArgs
                        {
                            Name = assemblerImageName,
                            Image = AssemblerImageTag,
                            Env = new[]
                            {
                                new EnvironmentVarArgs
                                {
                                    Name = "ASBConnectionString",
                                    SecretRef = asbConnectionSecret 
                                }
                            }
                        }
                    },
                    Scale = new ScaleArgs
                    {
                        MinReplicas = 0,
                        MaxReplicas = 10,
                        Rules = new List<ScaleRuleArgs>
                        {
                            new ScaleRuleArgs
                            {
                                Name = "assembler-queue-length",
                                Custom = new CustomScaleRuleArgs
                                {
                                    Type = "azure-servicebus",
                                    Metadata =
                                    {
                                        {"queueName", "crazybike-assembler"},
                                        {"messageCount", "10"}
                                    },
                                    Auth = new ScaleRuleAuthArgs
                                    {
                                        TriggerParameter = "connection",
                                        SecretRef = asbConnectionSecret
                                    }
                                }
                            }
                        }
                    }
                }
            }, new CustomResourceOptions
            {
                IgnoreChanges = new List<string> {"tags"},
                DependsOn = new InputList<Resource>{ assemblerBuildPushCommand }
            });
            
            var shipperName = $"{projectName}-{stackName}-ca-shipper";
            var shipper = new App.ContainerApp(shipperName, new App.ContainerAppArgs
            {
                Name = shipperName,
                ResourceGroupName = resourceGroup.Name,
                ManagedEnvironmentId = kubeEnv.Id,
                Configuration = new ConfigurationArgs
                {
                    Registries =
                    {
                        new RegistryCredentialsArgs
                        {
                            Server = containerRegistry.LoginServer,
                            Username = adminUsername,
                            PasswordSecretRef = $"{containerRegistryName}-admin-pwd"
                        }
                    },
                    Secrets = 
                    {
                        new App.Inputs.SecretArgs
                        {
                            Name = $"{containerRegistryName}-admin-pwd",
                            Value = adminPassword
                        },
                        new App.Inputs.SecretArgs
                        {
                            Name = asbConnectionSecret,
                            Value = ASBPrimaryConnectionString
                        }
                    }
                },
                Template = new TemplateArgs
                {
                    Containers = 
                    {
                        new App.Inputs.ContainerArgs
                        {
                            Name = shipperImageName,
                            Image = ShipperImageTag,
                            Env = new[]
                            {
                                new EnvironmentVarArgs
                                {
                                    Name = "ASBConnectionString",
                                    SecretRef = asbConnectionSecret
                                }
                            }
                        }
                    },
                    Scale = new ScaleArgs
                    {
                        MinReplicas = 0,
                        MaxReplicas = 10,
                        Rules = new List<ScaleRuleArgs>
                        {
                            new ScaleRuleArgs
                            {
                                Name = "shipper-queue-length",
                                Custom = new CustomScaleRuleArgs
                                {
                                    Type = "azure-servicebus",
                                    Metadata =
                                    {
                                        {"queueName", "crazybike-shipper"},
                                        {"messageCount", "10"}
                                    },
                                    Auth = new ScaleRuleAuthArgs
                                    {
                                        TriggerParameter = "connection",
                                        SecretRef = asbConnectionSecret
                                    }
                                }
                            }
                        }
                    }
                }
            }, new CustomResourceOptions
            {
                IgnoreChanges = new List<string> {"tags"},
                DependsOn = new InputList<Resource>{ shipperBuildPushCommand }
            });
            
            #endregion
        }

        static async Task<string> GetASBPrimaryConectionString(string resourceGroupName, string namespaceName)
        {
            var result = await ASB.ListNamespaceKeys.InvokeAsync(new ASB.ListNamespaceKeysArgs
            {
                AuthorizationRuleName = "RootManageSharedAccessKey",
                NamespaceName = namespaceName,
                ResourceGroupName = resourceGroupName
            });
            return result.PrimaryConnectionString;
        }
    }
}
