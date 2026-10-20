using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Newtonsoft.Json.Linq;
using QuantumCircuit.Connector;

internal static class AzureQuantumWorkspaceClientTests
{
    public static async Task RunAsync()
    {
        var originalEndpoint = Environment.GetEnvironmentVariable("AZURE_QUANTUM_ENDPOINT");
        var originalWorkspaceId = Environment.GetEnvironmentVariable("AZURE_QUANTUM_WORKSPACE_RESOURCE_ID");
        var originalTargetId = Environment.GetEnvironmentVariable("AZURE_QUANTUM_TARGET_ID");
        var originalTimeout = Environment.GetEnvironmentVariable("AZURE_QUANTUM_JOB_TIMEOUT_SECONDS");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_QUANTUM_ENDPOINT", null);
            Environment.SetEnvironmentVariable("AZURE_QUANTUM_WORKSPACE_RESOURCE_ID", null);
            Environment.SetEnvironmentVariable("AZURE_QUANTUM_TARGET_ID", null);
            var missingConfigurationRejected = false;
            try
            {
                await CreateClient(new WorkspaceHandler(new JObject())).RunCircuitAsync(CreateBellCircuit());
            }
            catch (AzureQuantumConfigurationException)
            {
                missingConfigurationRejected = true;
            }
            if (!missingConfigurationRejected)
            {
                throw new InvalidOperationException("The Azure Quantum client accepted missing workspace configuration.");
            }

            Environment.SetEnvironmentVariable("AZURE_QUANTUM_ENDPOINT", "https://westus.quantum.azure.com/");
            Environment.SetEnvironmentVariable(
                "AZURE_QUANTUM_WORKSPACE_RESOURCE_ID",
                "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.Quantum/workspaces/test");
            Environment.SetEnvironmentVariable("AZURE_QUANTUM_JOB_TIMEOUT_SECONDS", "5");
            await AssertRigettiAsync();
            await AssertIonQAsync();
            await AssertQuantinuumAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_QUANTUM_ENDPOINT", originalEndpoint);
            Environment.SetEnvironmentVariable("AZURE_QUANTUM_WORKSPACE_RESOURCE_ID", originalWorkspaceId);
            Environment.SetEnvironmentVariable("AZURE_QUANTUM_TARGET_ID", originalTargetId);
            Environment.SetEnvironmentVariable("AZURE_QUANTUM_JOB_TIMEOUT_SECONDS", originalTimeout);
        }

        Console.WriteLine("Azure Quantum in-host REST submission, native provider serialization, and result mapping passed.");
    }


    private static async Task AssertRigettiAsync()
    {
        Environment.SetEnvironmentVariable("AZURE_QUANTUM_TARGET_ID", "rigetti.sim.qvm");
        var handler = new WorkspaceHandler(new JObject { ["ro"] = new JArray(new JArray(0, 0), new JArray(1, 1)) });
        var result = await CreateClient(handler).RunCircuitAsync(CreateBellCircuit());
        AssertCount(result, "00", 1);
        AssertCount(result, "11", 1);
        if (handler.JobRequest?["providerId"]?.Value<string>() != "rigetti"
            || handler.UploadedContentType != "text/plain"
            || handler.JobRequest?["inputDataFormat"]?.Value<string>() != "rigetti.quil.v1"
            || handler.JobRequest?["outputDataFormat"]?.Value<string>() != "rigetti.quil-results.v1"
            || handler.JobRequest?["inputParams"]?["shots"]?.Value<int>() != 2
            || !handler.UploadedInput.Contains("DECLARE ro BIT[2]", StringComparison.Ordinal)
            || !handler.UploadedInput.Contains("CNOT 0 1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Rigetti job serialization or submission metadata was incorrect.");
        }
    }

    private static async Task AssertIonQAsync()
    {
        Environment.SetEnvironmentVariable("AZURE_QUANTUM_TARGET_ID", "ionq.simulator");
        var handler = new WorkspaceHandler(new JObject
        {
            ["histogram"] = new JObject { ["0"] = 0.5, ["3"] = 0.5 },
        });
        var result = await CreateClient(handler).RunCircuitAsync(CreateBellCircuit());
        AssertCount(result, "00", 1);
        AssertCount(result, "11", 1);
        var circuit = JObject.Parse(handler.UploadedInput);
        if (handler.JobRequest?["providerId"]?.Value<string>() != "IonQ"
            || handler.UploadedContentType != "application/json"
            || handler.JobRequest?["inputDataFormat"]?.Value<string>() != "ionq.circuit.v1"
            || circuit["qubits"]?.Value<int>() != 2
            || circuit["circuit"] is not JArray gates
            || gates.Count != 2
            || gates[1]?["gate"]?.Value<string>() != "cnot")
        {
            throw new InvalidOperationException("IonQ circuit serialization or submission metadata was incorrect.");
        }
    }

    private static async Task AssertQuantinuumAsync()
    {
        Environment.SetEnvironmentVariable("AZURE_QUANTUM_TARGET_ID", "quantinuum.sim.h2-1sc");
        var handler = new WorkspaceHandler(new JObject { ["ro"] = new JArray("00", "11") });
        var result = await CreateClient(handler).RunCircuitAsync(CreateBellCircuit());
        AssertCount(result, "00", 1);
        AssertCount(result, "11", 1);
        if (handler.JobRequest?["providerId"]?.Value<string>() != "quantinuum"
            || handler.UploadedContentType != "application/qasm"
            || handler.JobRequest?["inputDataFormat"]?.Value<string>() != "honeywell.openqasm.v1"
            || !handler.UploadedInput.Contains("OPENQASM 2.0;", StringComparison.Ordinal)
            || !handler.UploadedInput.Contains("cx q[0],q[1];", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Quantinuum OpenQASM serialization or submission metadata was incorrect.");
        }
    }

    private static AzureQuantumWorkspaceClient CreateClient(WorkspaceHandler handler) =>
        new(new StaticTokenCredential(), new HttpClient(handler));

    private static JObject CreateBellCircuit() => new()
    {
        ["initialState"] = "00",
        ["gates"] = new JArray
        {
            new JObject { ["gate"] = "H", ["target"] = 0 },
            new JObject { ["gate"] = "CNOT", ["control"] = 0, ["target"] = 1 },
        },
        ["shots"] = 2,
    };

    private static void AssertCount(JObject result, string state, int expected)
    {
        if (result["counts"]?[state]?.Value<int>() != expected
            || result["status"]?.Value<string>() != "Succeeded")
        {
            throw new InvalidOperationException($"Expected Azure Quantum count {state}={expected}.");
        }
    }

    private sealed class StaticTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1)));
    }

    private sealed class WorkspaceHandler : HttpMessageHandler
    {
        private readonly JObject results;

        public WorkspaceHandler(JObject results)
        {
            this.results = results;
        }

        public JObject JobRequest { get; private set; }

        public string UploadedInput { get; private set; }

        public string UploadedContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            if (uri.Host == "storage.test" && request.Method == HttpMethod.Put)
            {
                this.UploadedContentType = request.Content.Headers.ContentType?.ToString();
                this.UploadedInput = await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.Created);
            }

            if (uri.Host == "storage.test" && request.Method == HttpMethod.Get)
            {
                return Json(this.results.ToString(Newtonsoft.Json.Formatting.None));
            }

            const string workspacePath =
                "/subscriptions/00000000-0000-0000-0000-000000000000"
                + "/resourceGroups/rg/providers/Microsoft.Quantum/workspaces/test";
            if (uri.Host != "westus.quantum.azure.com"
                || !uri.AbsolutePath.StartsWith(workspacePath + "/", StringComparison.Ordinal)
                || uri.Query != "?api-version=2026-01-15-preview")
            {
                throw new InvalidOperationException(
                    $"Azure Quantum workspace requests must include the full workspace resource path: {uri}");
            }

            if (uri.AbsolutePath.EndsWith("/storage/sasUri", StringComparison.Ordinal))
            {
                if (request.Method != HttpMethod.Post
                    || uri.AbsolutePath != workspacePath + "/storage/sasUri")
                {
                    throw new InvalidOperationException("The Azure Quantum SAS request used an unexpected route or method.");
                }

                if (request.Headers.Authorization?.Parameter != "test-token")
                {
                    throw new InvalidOperationException("Azure Quantum workspace calls did not use the bearer token.");
                }

                var details = JObject.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                if (details["containerName"] == null)
                {
                    throw new InvalidOperationException("The Azure Quantum SAS request used an unexpected body shape.");
                }

                var blobName = details["blobName"]?.Value<string>();
                var sasUri = blobName == null
                    ? "https://storage.test/jobcontainer?sig=input"
                    : "https://storage.test/jobcontainer/outputData?sig=output";
                return Json(new JObject { ["sasUri"] = sasUri }.ToString(Newtonsoft.Json.Formatting.None));
            }

            if (uri.AbsolutePath.Contains("/jobs/", StringComparison.Ordinal)
                && request.Method == HttpMethod.Put)
            {
                this.JobRequest = JObject.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                return Json("{}");
            }

            if (uri.AbsolutePath.Contains("/jobs/", StringComparison.Ordinal)
                && request.Method == HttpMethod.Get)
            {
                return Json(new JObject
                {
                    ["status"] = "Succeeded",
                    ["outputDataUri"] = "https://storage.test/jobcontainer/outputData",
                }.ToString(Newtonsoft.Json.Formatting.None));
            }

            throw new InvalidOperationException($"Unexpected mocked Azure Quantum request: {request.Method} {uri}");
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }
}
