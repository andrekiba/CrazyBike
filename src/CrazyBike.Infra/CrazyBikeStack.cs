using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
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
using Storage = Pulumi.AzureNative.Storage;

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
        [Output] public Output<string> BuildContextBlobUrl {get; set;}
        [Output] public Output<string> BuyBuildOutput {get; set;}
        [Output] public Output<string> AssemblerBuildOutput {get; set;}
        [Output] public Output<string> ShipperBuildOutput {get; set;}

        #endregion
        
        public CrazyBikeStack()
        {
#if DEBUG
            /*
            while (!Debugger.IsAttached)
            {
                Thread.Sleep(100);
            }
            */
#endif            
            const string projectName = "crazybike";
            var stackName = Deployment.Instance.StackName;
            
            #region Resource Group
            
            var resourceGroupName = $"{projectName}-{stackName}-rg";
            var resourceGroup = new ResourceGroup(resourceGroupName, new ResourceGroupArgs
            {
                ResourceGroupName = resourceGroupName
            });

            #endregion
            
            #region Azure Storage

            var storageAccountName = $"{projectName}{stackName}st";
            var storageAccount = new Storage.StorageAccount(storageAccountName, new Storage.StorageAccountArgs
            {
                AccountName = storageAccountName,
                ResourceGroupName = resourceGroup.Name,
                Sku = new Storage.Inputs.SkuArgs
                {
                    Name = Storage.SkuName.Standard_LRS
                },
                Kind = Storage.Kind.StorageV2
            });
            
            const string dockerContainerName = "docker-build-archives";
            var dockerContainer = new Storage.BlobContainer(dockerContainerName, new Storage.BlobContainerArgs
            {
                ContainerName = dockerContainerName,
                AccountName = storageAccount.Name,
                ResourceGroupName = resourceGroup.Name,
                PublicAccess = Storage.PublicAccess.None,
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
            
            #region Docker build context and image tags
            
            var buildContext = Path.GetFullPath(Directory.GetParent(Directory.GetCurrentDirectory()).FullName);
            
            var excludedDirectories = new[] { "bin", "obj", ".idea", nameof(Infra) };
            var excludedFiles = new[] {".DS_Store", "appsettings.secret.json", "appsettings.development.json", ".override.yml"};
            
            const string buildContextTarName = "crazybike-build-context.tar.gz";
            var buildContextTar = buildContext.TarDirectory(Path.Combine(Path.GetTempPath(), buildContextTarName),
                excludedDirectories, excludedFiles);

            var buildContextBlob = new Storage.Blob(buildContextTarName, new Storage.BlobArgs
            {
                AccountName = storageAccount.Name,
                ContainerName = dockerContainer.Name,
                ResourceGroupName = resourceGroup.Name,
                Type = Storage.BlobType.Block,
                Source = new FileAsset(buildContextTar),
                //ContentMd5 = buildContextTar.CalculateMD5()
            }, new CustomResourceOptions
            {
                //DeleteBeforeReplace = true
            });
            
            BuildContextBlobUrl = SignedBlobReadUrl(buildContextBlob, dockerContainer, storageAccount, resourceGroup);
            
            var buyContext = Path.Combine(buildContext,"CrazyBike.Buy");
            var buyImageName = $"{projectName}-buy";
            var buyContextHash = buyContext.GenerateHash(excludedDirectories, excludedFiles);
            BuyImageTag = Output.Format($"{containerRegistry.LoginServer}/{buyImageName}:latest-{buyContextHash}");
            
            var assemblerContext = Path.Combine(buildContext,"CrazyBike.Assembler");
            var assemblerImageName = $"{projectName}-assembler";
            var assemblerContextHash = assemblerContext.GenerateHash(excludedDirectories, excludedFiles);
            AssemblerImageTag = Output.Format($"{containerRegistry.LoginServer}/{assemblerImageName}:latest-{assemblerContextHash}");
            
            var shipperContext = Path.Combine(buildContext,"CrazyBike.Shipper");
            var shipperImageName = $"{projectName}-shipper";
            var shipperContextHash = shipperContext.GenerateHash(excludedDirectories, excludedFiles);
            ShipperImageTag = Output.Format($"{containerRegistry.LoginServer}/{shipperImageName}:latest-{shipperContextHash}");
            
            #endregion 
            
            #region ACR commands
            
            /*
            const string azAcrBuildAndPush = "az acr build -r $REGISTRY -t $IMAGENAME -f $DOCKERFILE $CONTEXT";
            
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
            */
            
            #endregion
            
            #region ACR tasks
            
            const string buyBuildTaskName = "buy-build-task";
            var buyBuildTask = new ACR.Task(buyBuildTaskName, new TaskArgs
            {
                TaskName = buyBuildTaskName,
                RegistryName = containerRegistry.Name,
                ResourceGroupName = resourceGroup.Name,
                Status = ACR.TaskStatus.Enabled,
                IsSystemTask = false,
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
                    Architecture = Architecture.Amd64,
                    Os = OS.Linux
                },
                Step = new DockerBuildStepArgs
                {
                    ContextPath = BuildContextBlobUrl,
                    DockerFilePath = "Dockerfile.buy",
                    ImageNames = 
                    {
                        BuyImageTag
                    },
                    IsPushEnabled = true,
                    NoCache = false,
                    Type = "Docker"
                }
            });
            
            const string buyBuildTaskRunName = "buy-build-task-run";
            var buyBuildTaskRun = new TaskRun(buyBuildTaskRunName, new TaskRunArgs
            {
                //TaskRunName = buyBuildTaskRunName,
                RegistryName = containerRegistry.Name,
                ResourceGroupName = resourceGroup.Name,
                ForceUpdateTag = BuyImageTag,
                RunRequest = new TaskRunRequestArgs
                {
                    TaskId = buyBuildTask.Id,
                    Type = "TaskRunRequest"
                }
            });
            
            /*
            const string buyBuildTaskRunName = "buy-build-task-run";
            var buyBuildTaskRun = new TaskRun(buyBuildTaskRunName, new TaskRunArgs
            {
                TaskRunName = buyBuildTaskRunName,
                RegistryName = containerRegistry.Name,
                ResourceGroupName = resourceGroup.Name,
                ForceUpdateTag = BuyImageTag,
                RunRequest = new DockerBuildRequestArgs
                {
                    SourceLocation = BuildContextBlobUrl,
                    DockerFilePath = "Dockerfile.buy",
                    ImageNames = 
                    {
                        BuyImageTag
                    },
                    IsPushEnabled = true,
                    IsArchiveEnabled = true,
                    NoCache = false,
                    Type = "DockerBuildRequest",
                    Platform = new PlatformPropertiesArgs
                    {
                        Architecture = Architecture.Amd64,
                        Os = OS.Linux
                    }
                }
            });
            */
            
            const string assemblerBuildTaskRunName = "assembler-build-task-run";
            var assemblerBuildTaskRun = new TaskRun(assemblerBuildTaskRunName, new TaskRunArgs
            {
                TaskRunName = assemblerBuildTaskRunName,
                RegistryName = containerRegistry.Name,
                ResourceGroupName = resourceGroup.Name,
                ForceUpdateTag = AssemblerImageTag,
                RunRequest = new DockerBuildRequestArgs
                {
                    SourceLocation = BuildContextBlobUrl,
                    DockerFilePath = "Dockerfile.assembler",
                    ImageNames = 
                    {
                        AssemblerImageTag
                    },
                    IsPushEnabled = true,
                    IsArchiveEnabled = true,
                    NoCache = false,
                    Type = "DockerBuildRequest",
                    Platform = new PlatformPropertiesArgs
                    {
                        Architecture = Architecture.Amd64,
                        Os = OS.Linux
                    }
                }
            });
            
            const string shipperBuildTaskRunName = "shipper-build-task-run";
            var shipperBuildTaskRun = new TaskRun(shipperBuildTaskRunName, new TaskRunArgs
            {
                TaskRunName = shipperBuildTaskRunName,
                RegistryName = containerRegistry.Name,
                ResourceGroupName = resourceGroup.Name,
                ForceUpdateTag = ShipperImageTag,
                RunRequest = new DockerBuildRequestArgs
                {
                    SourceLocation = BuildContextBlobUrl,
                    DockerFilePath = "Dockerfile.shipper",
                    ImageNames = 
                    {
                        ShipperImageTag
                    },
                    IsPushEnabled = true,
                    IsArchiveEnabled = true,
                    NoCache = false,
                    Type = "DockerBuildRequest",
                    Platform = new PlatformPropertiesArgs
                    {
                        Architecture = Architecture.Amd64,
                        Os = OS.Linux
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
                    ActiveRevisionsMode = App.ActiveRevisionsMode.Single,
                    Ingress = new IngressArgs
                    {
                        External = true,
                        TargetPort = 80,
                        /*
                        Traffic = 
                        {
                            new TrafficWeightArgs
                            {
                                LatestRevision = false,
                                RevisionName =  resourceGroup.Name.Apply(rgName => GetLastRevision(rgName, buyName)),
                                Weight = 50
                            },
                            new TrafficWeightArgs
                            {
                                Label = "test",
                                LatestRevision = true,
                                Weight = 50
                            }
                        }
                        */
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
                        new SecretArgs
                        {
                            Name = $"{containerRegistryName}-admin-pwd",
                            Value = adminPassword
                        },
                        new SecretArgs
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
                        new ContainerArgs
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
                DependsOn = new InputList<Resource>{ buyBuildTaskRun }
            });
            BuyUrl = Output.Format($"https://{buy.Configuration.Apply(c => c.Ingress).Apply(i => i.Fqdn)}/swagger");
            
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
                        new SecretArgs
                        {
                            Name = $"{containerRegistryName}-admin-pwd",
                            Value = adminPassword
                        },
                        new SecretArgs
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
                        new ContainerArgs
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
                DependsOn = new InputList<Resource>{ assemblerBuildTaskRun }
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
                        new SecretArgs
                        {
                            Name = $"{containerRegistryName}-admin-pwd",
                            Value = adminPassword
                        },
                        new SecretArgs
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
                        new ContainerArgs
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
                DependsOn = new InputList<Resource>{ shipperBuildTaskRun }
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
        
        static Output<string> SignedBlobReadUrl(Storage.Blob blob, Storage.BlobContainer container, Storage.StorageAccount account, ResourceGroup resourceGroup)
        {
            var serviceSasToken = Storage.ListStorageAccountServiceSAS.Invoke(new Storage.ListStorageAccountServiceSASInvokeArgs
            {
                AccountName = account.Name,
                Protocols = Storage.HttpProtocol.Https,
                SharedAccessStartTime = "2022-05-01",
                SharedAccessExpiryTime = "2022-12-31",
                Resource = Storage.SignedResource.C,
                ResourceGroupName = resourceGroup.Name,
                Permissions = Storage.Permissions.R,
                CanonicalizedResource = Output.Format($"/blob/{account.Name}/{container.Name}"),
                ContentType = "application/json",
                CacheControl = "max-age=5",
                ContentDisposition = "inline",
                ContentEncoding = "deflate",
            }).Apply(blobSAS => blobSAS.ServiceSasToken);

            return Output.Format($"https://{account.Name}.blob.core.windows.net/{container.Name}/{blob.Name}?{serviceSasToken}");
        }

        static async Task<string> GetLastRevision(string resourceGroupName, string appName)
        {
            var result = await App.GetContainerApp.InvokeAsync(new App.GetContainerAppArgs
            {
                ResourceGroupName = resourceGroupName,
                Name = appName
            });
            return result.LatestRevisionName;
        }
    }
}
