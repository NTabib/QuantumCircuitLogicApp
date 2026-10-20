namespace QuantumCircuit.Connector
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading.Tasks;
    using Azure.Identity;
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
        public const string Backend = "AzureQuantum";

        private const uint BrandColor = 0x0078D4;
        private static readonly Uri IconUri = CreateIconUri();
        private readonly AzureQuantumWorkspaceClient workspaceClient;

        private readonly ServiceOperationApi serviceOperationApi;
        private readonly InsensitiveDictionary<ServiceOperation> apiOperations;
        private readonly List<ServiceOperation> expandedOperations;

        public AzureQuantumCircuitServiceOperationProvider()
            : this(new AzureQuantumWorkspaceClient())
        {
        }

        public AzureQuantumCircuitServiceOperationProvider(AzureQuantumWorkspaceClient workspaceClient)
        {
            this.workspaceClient = workspaceClient;
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

            if (connectionParameters == null || !connectionParameters.TryGetValue("backend", out var backend)
                || backend?.Type != JTokenType.String || backend.Value<string>() != Backend)
            {
                throw new ServiceOperationsProviderException(
                    HttpStatusCode.BadRequest,
                    ServiceOperationsErrorResponseCode.InvalidServiceProviderConnection,
                    $"Connection backend must be '{Backend}'.",
                    null);
            }

            var parameters = serviceOperationRequest?.Parameters ?? new InsensitiveDictionary<JToken>();
            var initialState = GetRequired(parameters, "initialState");
            var gates = GetRequired(parameters, "gates");
            if (initialState.Type != JTokenType.String || gates is not JArray)
            {
                throw InvalidParameter("initialState must be a bit string and gates must be an instruction array.");
            }

            var shots = 100;
            if (parameters.TryGetValue("shots", out var shotsToken)
                && shotsToken != null
                && shotsToken.Type != JTokenType.Null)
            {
                if (shotsToken.Type != JTokenType.Integer)
                {
                    throw InvalidParameter($"shots must be an integer between 1 and {QuantumCircuitExecutor.MaxShots}.");
                }

                shots = shotsToken.Value<int>();
            }
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
            try
            {
                var result = await this.workspaceClient.RunCircuitAsync(requestBody).ConfigureAwait(false);
                return new ServiceOperationResponse(result, HttpStatusCode.OK);
            }
            catch (ArgumentException ex)
            {
                throw InvalidParameter(ex.Message);
            }
            catch (TimeoutException ex)
            {
                throw new ServiceOperationsProviderException(
                    HttpStatusCode.GatewayTimeout,
                    ServiceOperationsErrorResponseCode.ServiceOperationFailed,
                    "The Azure Quantum job did not complete before the configured timeout.",
                    ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new ServiceOperationsProviderException(
                    HttpStatusCode.GatewayTimeout,
                    ServiceOperationsErrorResponseCode.ServiceOperationFailed,
                    "The Azure Quantum workspace or storage request timed out.",
                    ex);
            }
            catch (AzureQuantumApiException ex)
            {
                throw new ServiceOperationsProviderException(
                    HttpStatusCode.BadGateway,
                    ServiceOperationsErrorResponseCode.ServiceOperationFailed,
                    ex.Message,
                    ex);
            }
            catch (HttpRequestException ex)
            {
                throw new ServiceOperationsProviderException(
                    HttpStatusCode.BadGateway,
                    ServiceOperationsErrorResponseCode.ServiceOperationFailed,
                    "The Logic App host could not communicate with the Azure Quantum workspace or job storage.",
                    ex);
            }
            catch (AuthenticationFailedException ex)
            {
                throw new ServiceOperationsProviderException(
                    HttpStatusCode.ServiceUnavailable,
                    ServiceOperationsErrorResponseCode.ServiceOperationFailed,
                    "The Logic App managed identity could not obtain an Azure Quantum access token.",
                    ex);
            }
            catch (AzureQuantumConfigurationException ex)
            {
                throw new ServiceOperationsProviderException(
                    HttpStatusCode.ServiceUnavailable,
                    ServiceOperationsErrorResponseCode.InvalidServiceProviderConnection,
                    ex.Message,
                    ex);
            }
            catch (InvalidOperationException ex)
            {
                throw new ServiceOperationsProviderException(
                    HttpStatusCode.BadGateway,
                    ServiceOperationsErrorResponseCode.ServiceOperationFailed,
                    ex.Message,
                    ex);
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
                Description = "Submits gate-only circuits directly to Azure Quantum using the Logic App managed identity.",
                DisplayName = "Azure Quantum Circuit",
                IconUri = IconUri,
                Capabilities = new[] { ApiCapability.Actions },
                ConnectionParameters = new ConnectionParameters
                {
                    AdditionalParameters = new InsensitiveDictionary<ConnectionStringParameters>
                    {
                        { "backend", new ConnectionStringParameters
                            {
                                Type = ConnectionStringType.StringType,
                                ParameterSource = ConnectionParameterSource.AppConfiguration,
                                UIDefinition = new UIDefinition
                                {
                                    DisplayName = "Backend",
                                    Description = "Use AzureQuantum. Workspace, target, and endpoint are configured on the Logic App host.",
                                    Tooltip = Backend,
                                    Constraints = new Constraints { Required = "true", Default = Backend },
                                },
                            }
                        },
                    },
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
                Description = "Converts supported unitary gate instructions to the configured target provider's native circuit format, submits the Azure Quantum job, and returns measured counts.",
                Visibility = Visibility.Important,
                OperationType = OperationType.ServiceProvider,
                BrandColor = BrandColor,
                IconUri = IconUri,
            },
        };

        private static Uri CreateIconUri()
        {
            const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\"><rect x=\"1\" y=\"1\" width=\"30\" height=\"30\" rx=\"7\" fill=\"#E6F2FF\" stroke=\"#0078D4\" stroke-width=\"1.5\"/><path d=\"M5 11h5l3 10 5-14 4 11 2-7h3\" fill=\"none\" stroke=\"#0078D4\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/><circle cx=\"13\" cy=\"21\" r=\"2\" fill=\"#5C2D91\"/><circle cx=\"22\" cy=\"18\" r=\"2\" fill=\"#5C2D91\"/></svg>";
            var encodedSvg = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
            return new Uri($"data:image/svg+xml;base64,{encodedSvg}", UriKind.Absolute);
        }

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
                                { "backend", StringSchema("Backend", "Azure Quantum multi-provider native circuit adapter.") },
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
