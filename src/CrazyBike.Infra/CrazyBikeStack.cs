using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pulumi;
using Pulumi.AzureNative.App.Inputs;
using Pulumi.AzureNative.ContainerRegistry;
using Pulumi.AzureNative.ContainerRegistry.Inputs;
using Pulumi.AzureNative.OperationalInsights;
using Pulumi.AzureNative.OperationalInsights.Inputs;
using Pulumi.AzureNative.Resources;
using Pulumi.Docker;
using Pulumi.Docker.Inputs;
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
        [Output] public Output<string> BuyImageName { get; set; }
        [Output] public Output<string> AssemblerImageName { get; set; }
        [Output] public Output<string> ShipperImageName { get; set; }
        [Output] public Output<string> StdOut {get; set;}
        [Output] public Output<string> StdErr {get; set;}
        
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
            
            #region Docker images
            
            var dockerProvider = new Provider("azure_acr", new ProviderArgs
            {
               Host = OperatingSystem.IsWindows() ? "npipe:////.//pipe//docker_engine" : default,
               RegistryAuth = new ProviderRegistryAuthArgs
               {
                   Address = containerRegistry.LoginServer,
                   Username = adminUsername,
                   Password = adminPassword
               }
            });

            var buildContext = Path.GetFullPath(Directory.GetParent(Directory.GetCurrentDirectory()).FullName);
            
            var buyContext = Path.Combine(buildContext,"CrazyBike.Buy");
            var buyImageName = $"{projectName}-buy";
            var buyContextHash = buyContext.GenerateHash();
            /*
            var buyImage = new RegistryImage(buyImageName, new RegistryImageArgs
            {
                Name = Output.Format($"{containerRegistry.LoginServer}/{buyImageName}:latest-{buyContextHash}"),
                Build = new RegistryImageBuildArgs
                {
                    Dockerfile = "Dockerfile.buy",
                    Context = buildContext,
                    BuildId = buyContextHash
                }
            }, new CustomResourceOptions
            {
                Provider = dockerProvider
            });
            */
            var buyImage = new CustomImage(buyImageName, new CustomImageArgs
            {
                Context = buildContext,
                Dockerfile = "Dockerfile.buy",
                BuildId = buyContextHash,
                RegistryArgs = new CustomRegistryArgs
                {
                    Server = containerRegistry.LoginServer,
                    Username = adminUsername,
                    Password = adminPassword
                }
            });
            BuyImageName = buyImage.Name;

            var assemblerContext = Path.Combine(buildContext,"CrazyBike.Assembler");
            var assemblerImageName = $"{projectName}-assembler";
            var assemblerContextHash = assemblerContext.GenerateHash();
            /*
            var assemblerImage = new RegistryImage(assemblerImageName, new RegistryImageArgs
            {
                Name = Output.Format($"{containerRegistry.LoginServer}/{assemblerImageName}:latest-{assemblerContextHash}"),
                Build = new RegistryImageBuildArgs
                {
                    Dockerfile = "Dockerfile.assembler",
                    Context = buildContext,
                    BuildId = assemblerContextHash
                }
            }, new CustomResourceOptions
            {
                Provider = dockerProvider
            });
            */
            var assemblerImage = new CustomImage(assemblerImageName, new CustomImageArgs
            {
                Context = buildContext,
                Dockerfile = "Dockerfile.assembler",
                BuildId = assemblerContextHash,
                RegistryArgs = new CustomRegistryArgs
                {
                    Server = containerRegistry.LoginServer,
                    Username = adminUsername,
                    Password = adminPassword
                }
            });
            AssemblerImageName = assemblerImage.Name;
            
            var shipperContext = Path.Combine(buildContext,"CrazyBike.Shipper");
            var shipperImageName = $"{projectName}-shipper";
            var shipperContextHash = shipperContext.GenerateHash();
            /*
            var shipperImage = new RegistryImage(shipperImageName, new RegistryImageArgs
            {
                Name = Output.Format($"{containerRegistry.LoginServer}/{shipperImageName}:latest-{shipperContextHash}"),
                Build = new RegistryImageBuildArgs
                {
                    Dockerfile = "Dockerfile.shipper",
                    Context = buildContext,
                    BuildId = shipperContextHash
                }
            }, new CustomResourceOptions
            {
                Provider = dockerProvider
            });
            */
            var shipperImage = new CustomImage(shipperImageName, new CustomImageArgs
            {
                Context = buildContext,
                Dockerfile = "Dockerfile.shipper",
                BuildId = shipperContextHash,
                RegistryArgs = new CustomRegistryArgs
                {
                    Server = containerRegistry.LoginServer,
                    Username = adminUsername,
                    Password = adminPassword
                }
            });
            ShipperImageName = shipperImage.Name;
            
            /*
            var buyContext = $"{buildContext}/CrazyBike.Buy";
            var buyContextHash = GenerateHash(buyContext);
            var buyImageName = $"{projectName}-buy";
            var buyImage = new Image(buyImageName, new ImageArgs
            {
                ImageName = Output.Format($"{containerRegistry.LoginServer}/{buyImageName}:latest-{buyContextHash}"),
                Build = new DockerBuild
                {
                    Dockerfile = $"{buyContext}/Dockerfile",
                    Context = buildContext
                },
                Registry = new ImageRegistry
                {
                    Server = containerRegistry.LoginServer,
                    Username = adminUsername,
                    Password = adminPassword
                }
            });
            BuyImageName = buyImage.ImageName;
            
            var assemblerContext = $"{buildContext}/CrazyBike.Assembler";
            var assemblerContextHash = GenerateHash(assemblerContext);
            var assemblerImageName = $"{projectName}-assembler";
            var assemblerImage = new Image(assemblerImageName, new ImageArgs
            {
                ImageName = Output.Format($"{containerRegistry.LoginServer}/{assemblerImageName}:latest-{assemblerContextHash}"),
                Build = new DockerBuild
                {
                    Dockerfile = $"{assemblerContext}/Dockerfile",
                    Context = buildContext
                },
                Registry = new ImageRegistry
                {
                    Server = containerRegistry.LoginServer,
                    Username = adminUsername,
                    Password = adminPassword
                }
            });
            AssemblerImageName = assemblerImage.ImageName;
            
            var shipperContext = $"{buildContext}/CrazyBike.Shipper";
            var shipperContextHash = GenerateHash(shipperContext);
            var shipperImageName = $"{projectName}-shipper";
            var shipperImage = new Image(shipperImageName, new ImageArgs
            {
                ImageName = Output.Format($"{containerRegistry.LoginServer}/{shipperImageName}:latest-{shipperContextHash}"),
                Build = new DockerBuild
                {
                    Dockerfile = $"{shipperContext}/Dockerfile",
                    Context = buildContext
                },
                Registry = new ImageRegistry
                {
                    Server = containerRegistry.LoginServer,
                    Username = adminUsername,
                    Password = adminPassword
                }
            });
            ShipperImageName = shipperImage.ImageName;
            */
            #endregion
            
            #region ACR tasks
            
            var adosConfig = new Pulumi.Config("ados");
            var adosPat = adosConfig.RequireSecret("pat");
            
            var githubConfig = new Pulumi.Config("github");
            var githubPat = githubConfig.RequireSecret("pat");
            var githubRepoUrl = githubConfig.Require("repoUrl");
            
            const string assemblerBuildTaskName = "assembler-build-task";
            var assemblerBuildTask = new ACR.Task(assemblerBuildTaskName, new TaskArgs
            {
                TaskName = assemblerBuildTaskName,
                RegistryName = containerRegistry.Name,
                ResourceGroupName = resourceGroup.Name,
                Status = ACR.TaskStatus.Enabled,
                IsSystemTask = false,
                //LogTemplate = "acr/tasks:{{.Run.OS}}",
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
                    DockerFilePath = "./../Dockerfile.assembler",
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
                            Name = "githubSourceTrigger",
                            Status = TriggerStatus.Enabled,
                            SourceRepository = new SourcePropertiesArgs
                            {
                                Branch = stackName,
                                RepositoryUrl = githubRepoUrl,
                                SourceControlAuthProperties = new AuthInfoArgs
                                {
                                    Token = githubPat,
                                    TokenType = "PAT"
                                },
                                SourceControlType = "Github"
                            },
                            SourceTriggerEvents = 
                            {
                                "commit"
                            }
                        }
                    }
                }
            });
            
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
                            Image = buyImage.Name,
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
                IgnoreChanges = new List<string> {"tags"}
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
                            Image = assemblerImage.Name,
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
                IgnoreChanges = new List<string> {"tags"}
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
                            Image = shipperImage.Name,
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
                IgnoreChanges = new List<string> {"tags"}
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
