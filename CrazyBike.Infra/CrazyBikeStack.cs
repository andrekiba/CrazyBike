using System.Collections.Generic;
using System.Threading.Tasks;
using Pulumi;
using Pulumi.AzureNative.App.Inputs;
using Pulumi.AzureNative.ContainerRegistry;
using Pulumi.AzureNative.ContainerRegistry.Inputs;
using Pulumi.AzureNative.OperationalInsights;
using Pulumi.AzureNative.OperationalInsights.Inputs;
using Pulumi.AzureNative.Resources;
using Pulumi.Docker;
using Deployment = Pulumi.Deployment;
using ASB = Pulumi.AzureNative.ServiceBus;
using App = Pulumi.AzureNative.App;
using ACR = Pulumi.AzureNative.ContainerRegistry;

namespace CrazyBike.Infra
{
    internal class CrazyBikeStack : Stack
    {
        #region Output
        [Output] public Output<string> ASBPrimaryConnectionString { get; set; }
        [Output] public Output<string> BuyUrl { get; set; }
        [Output] public Output<string> BuyBaseImageName { get; set; }
        [Output] public Output<string> BuyImageName { get; set; }
        [Output] public Output<string> AssemblerBaseImageName { get; set; }
        [Output] public Output<string> AssemblerImageName { get; set; }
        [Output] public Output<string> ShipperBaseImageName { get; set; }
        [Output] public Output<string> ShipperImageName { get; set; }
        
        #endregion
        
        public CrazyBikeStack()
        {
            const string projectName = "crazybike";
            var stackName = Deployment.Instance.StackName;
            //var azureConfig = new Config("azure-native");
            //var location = azureConfig.Require("location");
            var ignoreChanges = new CustomResourceOptions
            {
                IgnoreChanges = new List<string> {"tags"}
            };
            
            #region Resource Group
            
            var resourceGroupName = $"{projectName}-{stackName}-rg";
            var resourceGroup = new ResourceGroup(resourceGroupName, new ResourceGroupArgs
            {
                //Location = location,
                ResourceGroupName = resourceGroupName
            }, ignoreChanges);

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
            }, ignoreChanges);
            
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
            
            #region Docker images
            
            var buyImageName = $"{projectName}-buy";
            var buyImage = new Image(buyImageName, new ImageArgs
            {
                ImageName = Output.Format($"{containerRegistry.LoginServer}/{buyImageName}:latest"),
                Build = new DockerBuild
                {
                    Dockerfile = "./../CrazyBike.Buy/Dockerfile",
                    Context = "./.."
                },
                Registry = new ImageRegistry
                {
                    Server = containerRegistry.LoginServer,
                    Username = adminUsername,
                    Password = adminPassword
                }
            });
            BuyBaseImageName = buyImage.BaseImageName;
            BuyImageName = buyImage.ImageName;
            
            var assemblerImageName = $"{projectName}-assembler";
            var assemblerImage = new Image(assemblerImageName, new ImageArgs
            {
                ImageName = Output.Format($"{containerRegistry.LoginServer}/{assemblerImageName}:latest"),
                Build = new DockerBuild
                {
                    Dockerfile = "./../CrazyBike.Assembler/Dockerfile",
                    Context = "./.."
                },
                Registry = new ImageRegistry
                {
                    Server = containerRegistry.LoginServer,
                    Username = adminUsername,
                    Password = adminPassword
                }
            });
            AssemblerBaseImageName = assemblerImage.BaseImageName;
            AssemblerImageName = assemblerImage.ImageName;
            
            var shipperImageName = $"{projectName}-shipper";
            var shipperImage = new Image(shipperImageName, new ImageArgs
            {
                ImageName = Output.Format($"{containerRegistry.LoginServer}/{shipperImageName}:latest"),
                Build = new DockerBuild
                {
                    Dockerfile = "./../CrazyBike.Shipper/Dockerfile",
                    Context = "./.."
                },
                Registry = new ImageRegistry
                {
                    Server = containerRegistry.LoginServer,
                    Username = adminUsername,
                    Password = adminPassword
                }
            });
            ShipperBaseImageName = shipperImage.BaseImageName;
            ShipperImageName = shipperImage.ImageName;
            
            #endregion
            
            #region ACR tasks
            /*
            var adosConfig = new Pulumi.Config("ados");
            var adosPat = adosConfig.RequireSecret("pat");
            
            const string assemblerBuildTaskName = "assembler-build";
            var assemblerBuildTask = new ACR.Task(assemblerBuildTaskName, new TaskArgs
            {
                TaskName = assemblerBuildTaskName,
                RegistryName = containerRegistry.Name,
                ResourceGroupName = resourceGroup.Name,
                Status = "Enabled",
                IsSystemTask = false,
                LogTemplate = "acr/tasks:{{.Run.OS}}",
                AgentConfiguration = new AgentPropertiesArgs
                {
                    Cpu = 2
                },
                Identity = new IdentityPropertiesArgs
                {
                    Type = ACR.ResourceIdentityType.SystemAssigned
                },
                Platform = new PlatformPropertiesArgs
                {
                    Architecture = "amd64",
                    Os = "Linux"
                },
                Step = new DockerBuildStepArgs
                {
                    
                    ContextPath = "./..",
                    DockerFilePath = "./../CrazyBike.Assembler/Dockerfile",
                    ImageNames = 
                    {
                        $"{assemblerImageName}:latest"
                    },
                    IsPushEnabled = true,
                    NoCache = false,
                    Type = "Docker"
                },
                Trigger = new TriggerPropertiesArgs
                {
                    SourceTriggers = 
                    {
                        new SourceTriggerArgs
                        {
                            Name = "adosSourceTrigger",
                            Status = "Enabled",
                            SourceRepository = new SourcePropertiesArgs
                            {
                                Branch = stackName,
                                RepositoryUrl = "https://elfoadosqa@dev.azure.com/elfoadosqa/CrazyBike/_git/CrazyBike",
                                SourceControlAuthProperties = new AuthInfoArgs
                                {
                                    Token = adosPat,
                                    TokenType = "PAT"
                                },
                                SourceControlType = "VisualStudioTeamService"
                            },
                            SourceTriggerEvents = 
                            {
                                "commit"
                            }
                        }
                    }
                }
            });
            */
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
                            Image = buyImage.ImageName,
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
                            Image = assemblerImage.ImageName,
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
                            Image = shipperImage.ImageName,
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
