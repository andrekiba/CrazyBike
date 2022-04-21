using System.Collections.Generic;
using System.Threading.Tasks;
using Pulumi;
using Pulumi.AzureNative.ContainerRegistry;
using Pulumi.AzureNative.ContainerRegistry.Inputs;
using Pulumi.AzureNative.OperationalInsights;
using Pulumi.AzureNative.OperationalInsights.Inputs;
using Pulumi.AzureNative.Resources;
using Pulumi.Docker;
using Deployment = Pulumi.Deployment;
using ASB = Pulumi.AzureNative.ServiceBus;
using App = Pulumi.AzureNative.App;

namespace CrazyBike.Infra
{
    internal class CrazyBikeStack : Stack
    {
        #region Output
        [Output] public Output<string> ASBPrimaryConnectionString { get; set; }
        [Output] public Output<string> BuyUrl { get; set; }
        
        [Output] public Output<string> BuyImageOut { get; set; }
        [Output] public Output<string> AssemblerImageOut { get; set; }
        [Output] public Output<string> ShipperImageOut { get; set; }
        
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
                ImageName = Output.Format($"{containerRegistry.LoginServer}/{buyImageName}:1.0.0"),
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
            BuyImageOut = buyImage.ImageName;
            
            var assemblerImageName = $"{projectName}-assembler";
            var assemblerImage = new Image(assemblerImageName, new ImageArgs
            {
                ImageName = Output.Format($"{containerRegistry.LoginServer}/{assemblerImageName}:1.0.0"),
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
            AssemblerImageOut = assemblerImage.ImageName;
            
            var shipperImageName = $"{projectName}-shipper";
            var shipperImage = new Image(shipperImageName, new ImageArgs
            {
                ImageName = Output.Format($"{containerRegistry.LoginServer}/{shipperImageName}:1.0.0"),
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
            ShipperImageOut = shipperImage.ImageName;
            
            #endregion 

            #region Container Apps

            var kubeEnvName = $"{projectName}-{stackName}-env";
            var kubeEnv = new App.ManagedEnvironment(kubeEnvName, new App.ManagedEnvironmentArgs
            {
                Name = kubeEnvName,
                ResourceGroupName = resourceGroup.Name,
                AppLogsConfiguration = new App.Inputs.AppLogsConfigurationArgs
                {
                    Destination = "log-analytics",
                    LogAnalyticsConfiguration = new App.Inputs.LogAnalyticsConfigurationArgs
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
                Configuration = new App.Inputs.ConfigurationArgs
                {
                    Ingress = new App.Inputs.IngressArgs
                    {
                        External = true,
                        TargetPort = 80
                    },
                    Registries =
                    {
                        new App.Inputs.RegistryCredentialsArgs
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
                        }
                    }
                },
                Template = new App.Inputs.TemplateArgs
                {
                    Containers = 
                    {
                        new App.Inputs.ContainerArgs
                        {
                            Name = buyImageName,
                            Image = buyImage.ImageName
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
                Configuration = new App.Inputs.ConfigurationArgs
                {
                    Registries =
                    {
                        new App.Inputs.RegistryCredentialsArgs
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
                        }
                    }
                },
                Template = new App.Inputs.TemplateArgs
                {
                    Containers = 
                    {
                        new App.Inputs.ContainerArgs
                        {
                            Name = assemblerImageName,
                            Image = assemblerImage.ImageName
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
                Configuration = new App.Inputs.ConfigurationArgs
                {
                    Registries =
                    {
                        new App.Inputs.RegistryCredentialsArgs
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
                        }
                    }
                },
                Template = new App.Inputs.TemplateArgs
                {
                    Containers = 
                    {
                        new App.Inputs.ContainerArgs
                        {
                            Name = shipperImageName,
                            Image = shipperImage.ImageName
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
