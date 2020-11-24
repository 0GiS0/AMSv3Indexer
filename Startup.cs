using AMSv3Indexer.Models;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Azure.Management.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest.Azure.Authentication;
using System;
using System.IO;

[assembly: FunctionsStartup(typeof(AMSv3Indexer.Startup))]
namespace AMSv3Indexer
{
    public class Startup : FunctionsStartup
    {

        public override void ConfigureAppConfiguration(IFunctionsConfigurationBuilder builder)
        {
            builder.ConfigurationBuilder
                   .SetBasePath(Directory.GetCurrentDirectory())
                   .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                   .Build();

            base.ConfigureAppConfiguration(builder);
        }

        public override void Configure(IFunctionsHostBuilder builder)
        {
            builder.Services.AddOptions<AMSSettings>()
                            .Configure<IConfiguration>((settings, configuration) =>
                            {
                                configuration.GetSection("AzureMediaServices").Bind(settings);
                            });

            builder.Services.AddSingleton<IAzureMediaServicesClient>(client =>
            {
                var _settings = new AMSSettings();
                builder.GetContext().Configuration.GetSection("AzureMediaServices").Bind(_settings);

                var clientCredentials = new ClientCredential(_settings.AadClientId, _settings.AadSecret);
                var credentials = ApplicationTokenProvider.LoginSilentAsync(_settings.AadTenantId, clientCredentials, ActiveDirectoryServiceSettings.Azure).GetAwaiter().GetResult();

                return new AzureMediaServicesClient(new Uri(_settings.ArmEndpoint), credentials) { SubscriptionId = _settings.SubscriptionId, LongRunningOperationRetryTimeout = 2 };
            });
        }
    }
}
