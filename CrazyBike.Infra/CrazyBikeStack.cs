using System.Collections.Generic;
using System.Threading.Tasks;
using Pulumi;
using Pulumi.AzureNative.ContainerRegistry;
using Pulumi.AzureNative.ContainerRegistry.Inputs;
using Pulumi.AzureNative.OperationalInsights;
using Pulumi.AzureNative.OperationalInsights.Inputs;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Web;
using Pulumi.AzureNative.Web.Inputs;
using Pulumi.Docker;
using Deployment = Pulumi.Deployment;
using ASB = Pulumi.AzureNative.ServiceBus;
using ContainerArgs = Pulumi.AzureNative.Web.Inputs.ContainerArgs;
using SecretArgs = Pulumi.AzureNative.Web.Inputs.SecretArgs;

namespace CrazyBike.Infra
{
    internal class CrazyBikeStack : Stack
    {
        #region Output
        [Output] public Output<string> ASBPrimaryConnectionString { get; set; }
        [Output] public Output<string> BuyUrl { get; set; }
        
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
                //Location = location,
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
            
            var containerRegistryName = $"{projectName}-{stackName}-cr";
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

            var buyAppImageName = $"{projectName}-buy";
            var buyAppImage = new Image(buyAppImageName, new ImageArgs
            {
                ImageName = Output.Format($"{containerRegistry.LoginServer}/{buyAppImageName}:v1"),
                Build = new DockerBuild { Context = $"./{buyAppImageName}" },
                Registry = new ImageRegistry
                {
                    Server = containerRegistry.LoginServer,
                    Username = adminUsername,
                    Password = adminPassword
                }
            });
            
            #endregion 

            #region Container Apps
            
            var kubeEnvName = $"{projectName}-{stackName}-env";
            var kubeEnv = new KubeEnvironment(kubeEnvName, new KubeEnvironmentArgs
            {
                Name = kubeEnvName,
                ResourceGroupName = resourceGroup.Name,
                //Type = "Managed",
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
            
            var buyAppName = $"{projectName}-{stackName}-ca-buy";
            var buyApp = new ContainerApp(buyAppName, new ContainerAppArgs
            {
                Name = buyAppName,
                ResourceGroupName = resourceGroup.Name,
                KubeEnvironmentId = kubeEnv.Id,
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
                            PasswordSecretRef = "pwd"
                        }
                    },
                    Secrets = 
                    {
                        new SecretArgs
                        {
                            Name = "pwd",
                            Value = adminPassword
                        }
                    }
                },
                Template = new TemplateArgs
                {
                    Containers = 
                    {
                        new ContainerArgs
                        {
                            Name = buyAppImageName,
                            Image = buyAppImage.ImageName
                        }
                    }
                }
            });
            
            BuyUrl = Output.Format($"https://{buyApp.Configuration.Apply(c => c.Ingress).Apply(i => i.Fqdn)}");
            
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
