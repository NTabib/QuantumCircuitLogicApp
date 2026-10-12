using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure.Workflows.ServiceProviders.Abstractions;
using Microsoft.WindowsAzure.ResourceStack.Common.Collections;
using Microsoft.WindowsAzure.ResourceStack.Common.Swagger.Entities;
using Newtonsoft.Json.Linq;
using QuantumCircuit.Connector;

internal static class AzureQuantumConnectionTests
{
    public static async Task RunAsync()
    {
        var provider = new AzureQuantumCircuitServiceOperationProvider();
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

        ServiceOperationRequest request = null;
        foreach (var connection in new InsensitiveDictionary<JToken>[]
        {
            null,
            new(),
            new() { { "backend", JValue.CreateNull() } },
            new() { { "backend", new JValue(1) } },
            new() { { "backend", new JValue("FullState") } },
        })
        {
            await AssertRejectedAsync(provider, connection, request, "Connection backend must be 'AzureQuantum'.");
        }

        var endpoint = Environment.GetEnvironmentVariable("AZURE_QUANTUM_BACKEND_URL");
        var key = Environment.GetEnvironmentVariable("AZURE_QUANTUM_BACKEND_FUNCTION_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_QUANTUM_BACKEND_URL", null);
            Environment.SetEnvironmentVariable("AZURE_QUANTUM_BACKEND_FUNCTION_KEY", null);
            var connection = new InsensitiveDictionary<JToken>
            {
                { "backend", new JValue(AzureQuantumCircuitServiceOperationProvider.Backend) },
            };
            await AssertRejectedAsync(provider, connection, request,
                "Configure AZURE_QUANTUM_BACKEND_URL and AZURE_QUANTUM_BACKEND_FUNCTION_KEY");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_QUANTUM_BACKEND_URL", endpoint);
            Environment.SetEnvironmentVariable("AZURE_QUANTUM_BACKEND_FUNCTION_KEY", key);
        }

        Console.WriteLine("Azure Quantum connection metadata and missing/invalid/valid connection checks passed.");
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
}
