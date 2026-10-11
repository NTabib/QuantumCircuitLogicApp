namespace QuantumCircuit.Connector
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.Azure.Workflows.ServiceProviders.Abstractions;
    using Microsoft.Azure.Workflows.ServiceProviders.WebJobs.Abstractions.Providers;
    using Microsoft.Azure.WebJobs.Description;
    using Microsoft.WindowsAzure.ResourceStack.Common.Collections;
    using Microsoft.WindowsAzure.ResourceStack.Common.Swagger.Entities;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    [ServiceOperationsProvider(Id = ServiceId, Name = ServiceName)]
    public sealed class AzureQuantumCircuitServiceOperationProvider : IServiceOperationsProvider
    {
        public const string ServiceName = "azureQuantumCircuit";
        public const string ServiceId = "/serviceProviders/azureQuantumCircuit";
        public const string RunCircuitOperationId = "runCircuit";

        private const uint BrandColor = 0x0078D4;
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(210),
        };

        private readonly ServiceOperationApi serviceOperationApi;
        private readonly InsensitiveDictionary<ServiceOperation> apiOperations;
        private readonly List<ServiceOperation> expandedOperations;

        public AzureQuantumCircuitServiceOperationProvider()
        {
            this.serviceOperationApi = BuildServiceOperationApi();
            var runCircuit = BuildOperation();
            this.apiOperations = new InsensitiveDictionary<ServiceOperation>
            {
                { RunCircuitOperationId, runCircuit },
            };
            this.expandedOperations = new List<ServiceOperation>
            {
                runCircuit.CloneWithManifest(BuildManifest()),
            };
        }

        public ServiceOperationApi GetService() => this.serviceOperationApi;

        public IEnumerable<ServiceOperation> GetOperations(bool expandManifest) =>
            expandManifest ? this.expandedOperations : this.apiOperations.Values;

        public string GetBindingConnectionInformation(string operationId, InsensitiveDictionary<JToken> connectionParameters) => null;

        public async Task<ServiceOperationResponse> InvokeOperation(
            string operationId,
            InsensitiveDictionary<JToken> connectionParameters,
            ServiceOperationRequest serviceOperationRequest)
        {
            if (operationId != RunCircuitOperationId)
            {
                throw new ServiceOperationsProviderException(
                    HttpStatusCode.BadRequest,
                    ServiceOperationsErrorResponseCode.ServiceOperationIdNotSupported,
                    $"Operation '{operationId}' is not supported by the {ServiceName} connector.",
                    null);
            }

            var endpoint = Environment.GetEnvironmentVariable("AZURE_QUANTUM_BACKEND_URL");
            var functionKey = Environment.GetEnvironmentVariable("AZURE_QUANTUM_BACKEND_FUNCTION_KEY");
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
                || (endpointUri.Scheme != Uri.UriSchemeHttps
                    && !(endpointUri.IsLoopback && endpointUri.Scheme == Uri.UriSchemeHttp))
                || string.IsNullOrWhiteSpace(functionKey))
            {
                throw new ServiceOperationsProviderException(
                    HttpStatusCode.ServiceUnavailable,
                    ServiceOperationsErrorResponseCode.InvalidServiceProviderConnection,
                    "Configure AZURE_QUANTUM_BACKEND_URL and AZURE_QUANTUM_BACKEND_FUNCTION_KEY in the Logic App host settings.",
                    null);
            }

            var parameters = serviceOperationRequest.Parameters ?? new InsensitiveDictionary<JToken>();
            var initialState = GetRequired(parameters, "initialState");
            var gates = GetRequired(parameters, "gates");
            if (initialState.Type != JTokenType.String || gates is not JArray)
            {
                throw InvalidParameter("initialState must be a bit string and gates must be an instruction array.");
            }

            var shots = parameters.TryGetValue("shots", out var shotsToken) && shotsToken?.Type != JTokenType.Null
                ? shotsToken.Value<int>()
                : 100;
            if (shots is < 1 or > QuantumCircuitExecutor.MaxShots)
            {
                throw InvalidParameter($"shots must be between 1 and {QuantumCircuitExecutor.MaxShots}.");
            }

            var requestBody = new JObject
            {
                ["initialState"] = initialState.DeepClone(),
                ["gates"] = gates.DeepClone(),
                ["shots"] = shots,
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, endpointUri)
            {
                Content = new StringContent(requestBody.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("x-functions-key", functionKey);

            HttpResponseMessage response;
            try
            {
                response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex)
            {
                throw new ServiceOperationsProviderException(
                    HttpStatusCode.GatewayTimeout,
                    ServiceOperationsErrorResponseCode.ServiceOperationFailed,
                    "The Azure Quantum backend did not respond before the connector timeout.",
                    ex);
            }

            using (response)
            {
                var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var detail = responseText.Length > 1000 ? responseText.Substring(0, 1000) : responseText;
                    throw new ServiceOperationsProviderException(
                        HttpStatusCode.BadGateway,
                        ServiceOperationsErrorResponseCode.ServiceOperationFailed,
                        $"The Azure Quantum backend returned HTTP {(int)response.StatusCode}: {detail}",
                        null);
                }

                JObject body;
                try
                {
                    body = JObject.Parse(responseText);
                }
                catch (JsonReaderException ex)
                {
                    throw new ServiceOperationsProviderException(
                        HttpStatusCode.BadGateway,
                        ServiceOperationsErrorResponseCode.ServiceOperationFailed,
                        "The Azure Quantum backend returned invalid JSON.",
                        ex);
                }

                return new ServiceOperationResponse(body, HttpStatusCode.OK);
            }
        }

        private static JToken GetRequired(InsensitiveDictionary<JToken> parameters, string name)
        {
            if (!parameters.TryGetValue(name, out var value) || value == null || value.Type == JTokenType.Null)
            {
                throw InvalidParameter($"The required parameter '{name}' is missing.");
            }

            return value;
        }

        private static ServiceOperationsProviderException InvalidParameter(string message) =>
            new ServiceOperationsProviderException(
                HttpStatusCode.BadRequest,
                ServiceOperationsErrorResponseCode.ServiceOperationInvalidParameter,
                message,
                null);

        private static ServiceOperationApi BuildServiceOperationApi() => new ServiceOperationApi
        {
            Name = ServiceName,
            Id = ServiceId,
            Type = DesignerApiType.ServiceProvider,
            Properties = new ServiceOperationApiProperties
            {
                BrandColor = BrandColor,
                Description = "Submits a gate-only circuit to an Azure Quantum target through the QDK-backed Azure Function backend.",
                DisplayName = "Azure Quantum Circuit",
                Capabilities = new[] { ApiCapability.Actions },
                ConnectionParameters = new ConnectionParameters
                {
                    AdditionalParameters = new InsensitiveDictionary<ConnectionStringParameters>(),
                },
            },
        };

        private static ServiceOperation BuildOperation() => new ServiceOperation
        {
            Name = RunCircuitOperationId,
            Id = RunCircuitOperationId,
            Type = RunCircuitOperationId,
            Properties = new ServiceOperationProperties
            {
                Api = BuildServiceOperationApi().GetFlattenedApi(),
                Summary = "Run a circuit on Azure Quantum",
                Description = "Converts supported unitary gate instructions to a Qiskit circuit, submits it to the configured Azure Quantum target, waits for completion, and returns measured counts.",
                Visibility = Visibility.Important,
                OperationType = OperationType.ServiceProvider,
                BrandColor = BrandColor,
            },
        };

        private static ServiceOperationManifest BuildManifest() => new ServiceOperationManifest
        {
            ConnectionReference = new ConnectionReferenceFormat
            {
                ReferenceKeyFormat = ConnectionReferenceKeyFormat.ServiceProvider,
            },
            Settings = new OperationManifestSettings
            {
                SecureData = new OperationManifestSettingWithOptions<SecureDataOptions>(),
                TrackedProperties = new OperationManifestSetting
                {
                    Scopes = new[] { OperationScope.Action },
                },
            },
            InputsLocation = new[] { InputsLocation.Inputs, InputsLocation.Parameters },
            Inputs = new SwaggerSchema
            {
                Type = SwaggerSchemaType.Object,
                Properties = new OrdinalDictionary<SwaggerSchema>
                {
                    {
                        "initialState", new SwaggerSchema
                        {
                            Type = SwaggerSchemaType.String,
                            Title = "Initial basis state",
                            Description = "Computational-basis bit string; left-most bit is qubit 0.",
                        }
                    },
                    {
                        "gates", new SwaggerSchema
                        {
                            Type = SwaggerSchemaType.Array,
                            Title = "Unitary gates",
                            Description = "Gate-only instructions: H, X, Y, Z, S, S_DAG/SDG, T, T_DAG/TDG, CNOT, CZ, SWAP, TOFFOLI/CCNOT, RX, RY, and RZ. Mid-circuit measurement, reset, IF, and reusable CALL are not supported on this hardware path.",
                            Items = new SwaggerSchema
                            {
                                Type = SwaggerSchemaType.Object,
                                Properties = new OrdinalDictionary<SwaggerSchema>
                                {
                                    { "gate", StringSchema("Gate", "Supported unitary gate name.") },
                                    { "target", new SwaggerSchema { Type = SwaggerSchemaType.Integer, Title = "Target qubit" } },
                                    { "control", new SwaggerSchema { Type = SwaggerSchemaType.Integer, Title = "Control qubit" } },
                                    { "control2", new SwaggerSchema { Type = SwaggerSchemaType.Integer, Title = "Second control qubit" } },
                                    { "angle", new SwaggerSchema { Type = SwaggerSchemaType.Number, Title = "Rotation angle in radians" } },
                                },
                                Required = new[] { "gate" },
                            },
                        }
                    },
                    {
                        "shots", new SwaggerSchema
                        {
                            Type = SwaggerSchemaType.Integer,
                            Title = "Shots",
                            Description = "Requested hardware samples (1-1000).",
                            Default = 100,
                        }
                    },
                },
                Required = new[] { "initialState", "gates" },
            },
            Outputs = new SwaggerSchema
            {
                Type = SwaggerSchemaType.Object,
                Properties = new OrdinalDictionary<SwaggerSchema>
                {
                    {
                        "body", new SwaggerSchema
                        {
                            Type = SwaggerSchemaType.Object,
                            Title = "Azure Quantum job result",
                            Properties = new OrdinalDictionary<SwaggerSchema>
                            {
                                { "backend", StringSchema("Backend", "Azure Quantum Qiskit adapter.") },
                                { "provider", StringSchema("Provider", "Azure Quantum provider identifier.") },
                                { "targetId", StringSchema("Target", "Azure Quantum provider target ID.") },
                                { "jobId", StringSchema("Job ID", "Submitted Azure Quantum job.") },
                                { "status", StringSchema("Status", "Completed Azure Quantum job status.") },
                                { "qubitCount", IntegerSchema("Qubit count", "Circuit width.") },
                                { "gateCount", IntegerSchema("Gate count", "Number of submitted unitary gates.") },
                                { "shots", IntegerSchema("Shots", "Number of measurement samples returned.") },
                                { "requestedShots", IntegerSchema("Requested shots", "Number of measurement samples requested.") },
                                { "sample", StringSchema("Sample", "Most frequent result, in qubit-0-first order.") },
                                { "counts", new SwaggerSchema { Type = SwaggerSchemaType.Object, Title = "Measurement counts" } },
                                { "probabilities", new SwaggerSchema { Type = SwaggerSchemaType.Object, Title = "Empirical probabilities" } },
                            },
                        }
                    },
                },
            },
            Connector = BuildServiceOperationApi(),
        };

        private static SwaggerSchema StringSchema(string title, string description) =>
            new SwaggerSchema { Type = SwaggerSchemaType.String, Title = title, Description = description };

        private static SwaggerSchema IntegerSchema(string title, string description) =>
            new SwaggerSchema { Type = SwaggerSchemaType.Integer, Title = title, Description = description };
    }

    [Extension("AzureQuantumCircuitServiceProvider", configurationSection: "AzureQuantumCircuitServiceProvider")]
    public sealed class AzureQuantumCircuitServiceProvider : Microsoft.Azure.WebJobs.Host.Config.IExtensionConfigProvider
    {
        public AzureQuantumCircuitServiceProvider(
            ServiceOperationsProvider serviceOperationsProvider,
            AzureQuantumCircuitServiceOperationProvider operationsProvider)
        {
            serviceOperationsProvider.RegisterService(
                serviceName: AzureQuantumCircuitServiceOperationProvider.ServiceName,
                serviceOperationsProviderId: AzureQuantumCircuitServiceOperationProvider.ServiceId,
                serviceOperationsProviderInstance: operationsProvider);
        }

        public void Initialize(Microsoft.Azure.WebJobs.Host.Config.ExtensionConfigContext context)
        {
        }
    }
}
