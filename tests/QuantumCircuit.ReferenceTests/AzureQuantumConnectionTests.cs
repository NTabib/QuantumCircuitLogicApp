using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.Workflows.ServiceProviders.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.WindowsAzure.ResourceStack.Common.Collections;
using Microsoft.WindowsAzure.ResourceStack.Common.Swagger.Entities;
using Newtonsoft.Json.Linq;
using QuantumCircuit.Connector;

internal static class AzureQuantumConnectionTests
{
    public static async Task RunAsync()
    {
        var constructors = typeof(AzureQuantumWorkspaceClient).GetConstructors();
        if (constructors.Length != 1 || constructors[0].GetParameters().Length != 0)
        {
            throw new InvalidOperationException(
                "Runtime activation must expose only the default Azure Quantum credential constructor.");
        }

        var services = new ServiceCollection();
        services.AddSingleton<TokenCredential>(_ => null);
        services.AddSingleton<HttpClient>();
        using (var activationServices = services.BuildServiceProvider())
        {
            _ = ActivatorUtilities.CreateInstance<AzureQuantumWorkspaceClient>(activationServices);
        }

        new QuantumCircuitStartup().Configure(new TestWebJobsBuilder(services));
        using var hostServices = services.BuildServiceProvider();
        var client = hostServices.GetRequiredService<AzureQuantumWorkspaceClient>();
        if (!ReferenceEquals(client, hostServices.GetRequiredService<AzureQuantumWorkspaceClient>()))
        {
            throw new InvalidOperationException("The Azure Quantum workspace client must be a host singleton.");
        }

        var provider = hostServices.GetRequiredService<AzureQuantumCircuitServiceOperationProvider>();
        if (provider.GetOperations(false).Count() != 1
            || !provider.GetOperations(false).Any(operation =>
                operation.Name == AzureQuantumCircuitServiceOperationProvider.RunCircuitOperationId))
        {
            throw new InvalidOperationException("The Azure Quantum connector must expose only runCircuit.");
        }
        if (provider.GetOperations(true).Count() != 1)
        {
            throw new InvalidOperationException("Azure Quantum must publish a manifest only for runCircuit.");
        }

        if (provider.GetService().Properties.ConnectionParameters is not ConnectionParameters parameters)
        {
            throw new InvalidOperationException("Azure Quantum connection metadata is missing.");
        }
        if (provider.GetService().Properties.IconUri is not Uri icon
            || icon.Scheme != "data"
            || !icon.OriginalString.StartsWith("data:image/svg+xml;base64,", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Azure Quantum provider must expose an embedded SVG icon.");
        }

        var metadata = parameters.AdditionalParameters["backend"];
        if (metadata.UIDefinition.Constraints.Required != "true"
            || metadata.UIDefinition.Constraints.Default?.ToString() != AzureQuantumCircuitServiceOperationProvider.Backend)
        {
            throw new InvalidOperationException("Azure Quantum backend connection metadata must be required with the correct default.");
        }

        var connectionPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "QuantumCircuit.LogicApp", "connections.json"));
        var connections = JObject.Parse(File.ReadAllText(connectionPath));
        if (connections["serviceProviderConnections"]?["azureQuantumCircuit"]?["parameterValues"]?["backend"]?.Value<string>()
            != AzureQuantumCircuitServiceOperationProvider.Backend)
        {
            throw new InvalidOperationException("connections.json must contain the required Azure Quantum backend value.");
        }

        var workflowPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "QuantumCircuit.LogicApp",
            "HybridProductionSchedulingWorkflow", "workflow.json"));
        var workflow = JObject.Parse(File.ReadAllText(workflowPath));
        var actions = workflow["definition"]?["actions"];
        var quantumAction = actions?["Run_QAOA_candidate"];
        if (quantumAction?["type"]?.Value<string>() != "ServiceProvider"
            || quantumAction["inputs"]?["serviceProviderConfiguration"]?["connectionName"]?.Value<string>() != "azureQuantumCircuit"
            || quantumAction["inputs"]?["serviceProviderConfiguration"]?["operationId"]?.Value<string>()
                != AzureQuantumCircuitServiceOperationProvider.RunCircuitOperationId
            || actions?["Greedy_baseline"]?["type"]?.Value<string>() != "Compose"
            || actions?["Select_feasible_measurement"]?["type"]?.Value<string>() != "Compose"
            || workflow.ToString().Contains("runHybridSchedule", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Scheduling logic must live in the workflow and use only runCircuit for quantum execution.");
        }

        foreach (var connection in new InsensitiveDictionary<JToken>[]
        {
            null,
            new(),
            new() { { "backend", JValue.CreateNull() } },
            new() { { "backend", new JValue(1) } },
            new() { { "backend", new JValue("FullState") } },
        })
        {
            await AssertRejectedAsync(provider, connection, null, "Connection backend must be 'AzureQuantum'.");
        }

        Console.WriteLine("Azure Quantum connection metadata and missing/invalid connection checks passed.");
    }

    private static async Task AssertRejectedAsync(
        AzureQuantumCircuitServiceOperationProvider provider,
        InsensitiveDictionary<JToken> connection,
        ServiceOperationRequest request,
        string expectedMessage)
    {
        try
        {
            await provider.InvokeOperation(AzureQuantumCircuitServiceOperationProvider.RunCircuitOperationId, connection, request);
        }
        catch (ServiceOperationsProviderException error) when (error.Message.Contains(expectedMessage, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException($"Expected connector error: {expectedMessage}");
    }

    private sealed class TestWebJobsBuilder : IWebJobsBuilder
    {
        public TestWebJobsBuilder(IServiceCollection services)
        {
            this.Services = services;
        }

        public IServiceCollection Services { get; }
    }
}
