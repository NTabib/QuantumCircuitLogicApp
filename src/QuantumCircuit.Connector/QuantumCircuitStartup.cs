using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuantumCircuit.Connector;

[assembly: WebJobsStartup(typeof(QuantumCircuitStartup))]

namespace QuantumCircuit.Connector
{
    using Microsoft.Azure.WebJobs.Description;
    using Microsoft.Azure.WebJobs.Host.Config;
    using Microsoft.Azure.Workflows.ServiceProviders.Abstractions;

    /// <summary>
    /// Entry point discovered by the Azure Functions host (which runs Logic Apps Standard).
    /// Registers the extension and the operations provider in dependency injection.
    /// </summary>
    public class QuantumCircuitStartup : IWebJobsStartup
    {
        public void Configure(IWebJobsBuilder builder)
        {
            builder.AddExtension<QuantumCircuitServiceProvider>();
            builder.AddExtension<QuantumCircuitBuilderServiceProvider>();
            builder.Services.TryAddSingleton<QuantumCircuitServiceOperationProvider>();
            builder.Services.TryAddSingleton<QuantumCircuitBuilderServiceOperationProvider>();
        }
    }

    /// <summary>
    /// WebJobs extension that registers the Quantum Circuit service provider with the
    /// Logic Apps built-in connector registry.
    /// </summary>
    [Extension("QuantumCircuitServiceProvider", configurationSection: "QuantumCircuitServiceProvider")]
    public class QuantumCircuitServiceProvider : IExtensionConfigProvider
    {
        public QuantumCircuitServiceProvider(
            ServiceOperationsProvider serviceOperationsProvider,
            QuantumCircuitServiceOperationProvider operationsProvider)
        {
            serviceOperationsProvider.RegisterService(
                serviceName: QuantumCircuitServiceOperationProvider.ServiceName,
                serviceOperationsProviderId: QuantumCircuitServiceOperationProvider.ServiceId,
                serviceOperationsProviderInstance: operationsProvider);
        }

        public void Initialize(ExtensionConfigContext context)
        {
        }
    }
}
