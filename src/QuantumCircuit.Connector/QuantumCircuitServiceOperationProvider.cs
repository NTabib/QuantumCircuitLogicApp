namespace QuantumCircuit.Connector
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;
    using Microsoft.Azure.Workflows.ServiceProviders.Abstractions;
    using Microsoft.Azure.Workflows.ServiceProviders.WebJobs.Abstractions.Providers;
    using Microsoft.WindowsAzure.ResourceStack.Common.Collections;
    using Microsoft.WindowsAzure.ResourceStack.Common.Swagger.Entities;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Defines the "Quantum Circuit" built-in connector (API, operations, input/output schemas)
    /// and executes complete gate sequences through the Q# circuit dispatcher.
    /// </summary>
    [ServiceOperationsProvider(Id = ServiceId, Name = ServiceName)]
    public class QuantumCircuitServiceOperationProvider : IServiceOperationsProvider
    {
        public const string ServiceName = "quantumCircuit";

        public const string ServiceId = "/serviceProviders/quantumCircuit";

        private const string Simulator = "FullState";

        public const string RunCircuitOperationId = "runCircuit";


        private const uint BrandColor = 0x5C2D91;

        private static readonly Uri IconUri = new Uri("https://raw.githubusercontent.com/microsoft/qsharp/main/vscode/resources/qsharp.png");
        private static readonly Uri RunCircuitIconUri = CreateRunCircuitIconUri();

        private readonly ServiceOperationApi serviceOperationApi;

        private readonly InsensitiveDictionary<ServiceOperation> apiOperations;

        private readonly List<ServiceOperation> expandedOperations;

        public QuantumCircuitServiceOperationProvider()
        {
            this.serviceOperationApi = BuildServiceOperationApi();

            var runCircuit = this.BuildOperation(RunCircuitOperationId, "Run a quantum circuit", "Executes ordered gates, named measurements, resets, and measurement-conditioned gate blocks in one Q# shot.", RunCircuitIconUri);

            this.apiOperations = new InsensitiveDictionary<ServiceOperation>
            {
                { RunCircuitOperationId, runCircuit },
            };

            this.expandedOperations = new List<ServiceOperation>
            {
                runCircuit.CloneWithManifest(this.BuildRunCircuitManifest()),
            };
        }

        public ServiceOperationApi GetService() => this.serviceOperationApi;

        public IEnumerable<ServiceOperation> GetOperations(bool expandManifest) =>
            expandManifest ? this.expandedOperations : this.apiOperations.Values;

        /// <summary>Only used by trigger-based connectors (Azure Functions bindings). This connector exposes actions only.</summary>
        public string GetBindingConnectionInformation(string operationId, InsensitiveDictionary<JToken> connectionParameters) => null;

        public async Task<ServiceOperationResponse> InvokeOperation(string operationId, InsensitiveDictionary<JToken> connectionParameters, ServiceOperationRequest serviceOperationRequest)
        {
            if (connectionParameters == null || !connectionParameters.TryGetValue("simulator", out var simulator)
                || simulator?.Type != JTokenType.String || simulator.Value<string>() != Simulator)
            {
                throw new ServiceOperationsProviderException(
                    HttpStatusCode.BadRequest,
                    ServiceOperationsErrorResponseCode.InvalidServiceProviderConnection,
                    $"Connection simulator must be '{Simulator}'.",
                    null);
            }

            var parameters = serviceOperationRequest.Parameters ?? new InsensitiveDictionary<JToken>();

            try
            {
                switch (operationId)
                {
                    case RunCircuitOperationId:
                    {
                        var initialState = GetRequired(parameters, "initialState").Value<string>();
                        if (GetRequired(parameters, "gates") is not JArray gates)
                        {
                            throw new ArgumentException("gates must be an array of circuit instructions.", "gates");
                        }

                        var operations = parameters.TryGetValue("operations", out var operationToken) && operationToken.Type != JTokenType.Null
                            ? operationToken as JArray ?? throw new ArgumentException("operations must be an array of reusable operation definitions.", "operations")
                            : new JArray();
                        var shots = parameters.TryGetValue("shots", out var circuitShots) && circuitShots.Type != JTokenType.Null
                            ? circuitShots.Value<int>()
                            : 100;
                        var result = await QuantumCircuitExecutor.RunAsync(initialState, gates, operations, shots).ConfigureAwait(false);
                        var probabilities = result.Counts.ToDictionary(entry => entry.Key, entry => (double)entry.Value / result.Shots);
                        var body = new JObject
                        {
                            ["simulator"] = Simulator,
                            ["inputState"] = $"|{result.InputState}⟩",
                            ["sample"] = result.Sample,
                            ["measuredState"] = $"|{result.Sample}⟩",
                            ["qubitCount"] = result.QubitCount,
                            ["gateCount"] = result.GateCount,
                            ["shots"] = result.Shots,
                            ["measurements"] = JArray.FromObject(result.Measurements),
                            ["classicalMeasurements"] = JArray.FromObject(result.ClassicalMeasurements),
                            ["counts"] = JObject.FromObject(result.Counts),
                            ["probabilities"] = JObject.FromObject(probabilities),
                        };

                        return new ServiceOperationResponse(body, HttpStatusCode.OK);
                    }
                    default:
                        throw new ServiceOperationsProviderException(
                            HttpStatusCode.BadRequest,
                            ServiceOperationsErrorResponseCode.ServiceOperationIdNotSupported,
                            $"Operation '{operationId}' is not supported by the {ServiceName} connector.",
                            null);
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is InvalidCastException)
            {
                throw new ServiceOperationsProviderException(
                    HttpStatusCode.BadRequest,
                    ServiceOperationsErrorResponseCode.ServiceOperationInvalidParameter,
                    ex.Message,
                    ex);
            }
        }

        private static JToken GetRequired(InsensitiveDictionary<JToken> parameters, string name)
        {
            if (!parameters.TryGetValue(name, out var value) || value == null || value.Type == JTokenType.Null)
            {
                throw new ArgumentException($"The required parameter '{name}' is missing.", name);
            }

            return value;
        }

        private static ServiceOperationApi BuildServiceOperationApi() => new ServiceOperationApi
        {
            Name = ServiceName,
            Id = ServiceId,
            Type = DesignerApiType.ServiceProvider,
            Properties = new ServiceOperationApiProperties
            {
                BrandColor = BrandColor,
                Description = "Expands typed gate operations and executes measurement, reset, and bounded feed-forward circuits on Q# FullState.",
                DisplayName = "Quantum Circuit (Q#)",
                IconUri = IconUri,
                Capabilities = new[] { ApiCapability.Actions },
                ConnectionParameters = new ConnectionParameters
                {
                    AdditionalParameters = new InsensitiveDictionary<ConnectionStringParameters>
                    {
                        { "simulator", new ConnectionStringParameters
                            {
                                Type = ConnectionStringType.StringType,
                                ParameterSource = ConnectionParameterSource.AppConfiguration,
                                UIDefinition = new UIDefinition
                                {
                                    DisplayName = "Simulator",
                                    Description = "Use FullState. This executor supports only the Q# full-state simulator.",
                                    Tooltip = Simulator,
                                    Constraints = new Constraints { Required = "true", Default = Simulator },
                                },
                            }
                        },
                    },
                },
            },
        };

        private static Uri CreateRunCircuitIconUri()
        {
            const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\"><rect x=\"1\" y=\"1\" width=\"30\" height=\"30\" rx=\"7\" fill=\"#F3E8FF\" stroke=\"#5C2D91\" stroke-width=\"1.5\"/><path d=\"M8 10h7M8 16h5M8 22h7\" stroke=\"#5C2D91\" stroke-width=\"2\" stroke-linecap=\"round\"/><path d=\"m17 10 8 6-8 6z\" fill=\"#5C2D91\"/></svg>";
            var encodedSvg = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
            return new Uri($"data:image/svg+xml;base64,{encodedSvg}", UriKind.Absolute);
        }

        private ServiceOperation BuildOperation(string id, string summary, string description, Uri iconUri) => new ServiceOperation
        {
            Name = id,
            Id = id,
            Type = id,
            Properties = new ServiceOperationProperties
            {
                Api = this.serviceOperationApi.GetFlattenedApi(),
                Summary = summary,
                Description = description,
                Visibility = Visibility.Important,
                OperationType = OperationType.ServiceProvider,
                BrandColor = BrandColor,
                IconUri = iconUri,
            },
        };

        private ServiceOperationManifest BuildRunCircuitManifest() => this.BuildManifest(
            inputs: new SwaggerSchema
            {
                Type = SwaggerSchemaType.Object,
                Properties = new OrdinalDictionary<SwaggerSchema>
                {
                    {
                        "initialState", new SwaggerSchema
                        {
                            Type = SwaggerSchemaType.String,
                            Title = "Initial basis state",
                            Description = $"Bit string for the initial register (left-most character is qubit 0; maximum {QuantumCircuitExecutor.MaxQubits} qubits).",
                        }
                    },
                    {
                        "gates", new SwaggerSchema
                        {
                            Type = SwaggerSchemaType.Array,
                            Title = "Circuit instructions",
                            Description = "Ordered instructions: gates H, X, Y, Z, S, S_DAG, T, T_DAG, CNOT, CZ, SWAP, TOFFOLI, RX, RY, RZ; MEASURE with target and measurement name; RESET with target; or IF with an earlier measurement, Boolean equals, and then/optional else gate arrays. Rotations use radians. IF blocks are one level deep.",
                            Items = new SwaggerSchema
                            {
                                Type = SwaggerSchemaType.Object,
                                Properties = new OrdinalDictionary<SwaggerSchema>
                                {
                                    { "gate", StringSchema("Gate", "Gate name or CALL.") },
                                    { "target", new SwaggerSchema { Title = "Target or parameter placeholder", Description = "Integer qubit index for primitive gates." } },
                                    { "control", new SwaggerSchema { Title = "Control", Description = "Integer control index for controlled gates." } },
                                    { "control2", new SwaggerSchema { Title = "Second control", Description = "Integer second control for Toffoli." } },
                                    { "angle", new SwaggerSchema { Type = SwaggerSchemaType.Number, Title = "Angle", Description = "Rotation angle in radians for RX/RY/RZ." } },
                                    { "operation", StringSchema("Reusable operation", "Name of the operation to call when gate is CALL.") },
                                    { "arguments", new SwaggerSchema { Type = SwaggerSchemaType.Object, Title = "Typed arguments", Description = "Arguments declared by the named operation." } },
                                    { "measurement", StringSchema("Measurement name", "Required for MEASURE; unique within this circuit.") },
                                    { "equals", new SwaggerSchema { Type = SwaggerSchemaType.Boolean, Title = "Expected measurement", Description = "Boolean value used by an IF instruction." } },
                                    { "then", new SwaggerSchema { Type = SwaggerSchemaType.Array, Title = "If branch", Items = new SwaggerSchema { Type = SwaggerSchemaType.Object } } },
                                    { "else", new SwaggerSchema { Type = SwaggerSchemaType.Array, Title = "Else branch", Items = new SwaggerSchema { Type = SwaggerSchemaType.Object } } },
                                },
                                Required = new[] { "gate" },
                            },
                        }
                    },
                    {
                        "operations", new SwaggerSchema
                        {
                            Type = SwaggerSchemaType.Array,
                            Title = "Reusable operation definitions",
                            Description = "Optional named gate templates with typed Int and Double parameters, as returned by the circuit builder.",
                            Items = new SwaggerSchema { Type = SwaggerSchemaType.Object, Description = "Reusable operation definition." },
                        }
                    },
                    {
                        "shots", new SwaggerSchema
                        {
                            Type = SwaggerSchemaType.Integer,
                            Title = "Shots",
                            Description = $"Complete-circuit executions (1-{QuantumCircuitExecutor.MaxShots}).",
                            Default = 100,
                        }
                    },
                },
                Required = new[] { "initialState", "gates" },
            },
            outputs: new OrdinalDictionary<SwaggerSchema>
            {
                { "simulator", StringSchema("Simulator", "Q# state-vector simulator.") },
                { "inputState", StringSchema("Input state", "Initial computational-basis state.") },
                { "sample", StringSchema("Sample", "One measured output bit string.") },
                { "measuredState", StringSchema("Measured state", "Ket notation of the sample.") },
                { "qubitCount", IntegerSchema("Qubit count", "Number of qubits in the circuit.") },
                { "gateCount", IntegerSchema("Gate count", "Number of unitary gate instructions after reusable operations expand; both IF branches count toward this value.") },
                { "shots", IntegerSchema("Shots", "Number of complete circuit executions.") },
                { "measurements", new SwaggerSchema
                    {
                        Type = SwaggerSchemaType.Array,
                        Title = "Typed measurement samples",
                        Description = "One Boolean array per shot; true represents Q# Result.One.",
                        Items = new SwaggerSchema { Type = SwaggerSchemaType.Array, Items = new SwaggerSchema { Type = SwaggerSchemaType.Boolean } },
                    }
                },
                { "classicalMeasurements", new SwaggerSchema { Type = SwaggerSchemaType.Array, Title = "Named mid-circuit results", Description = "One object per shot. Boolean values are named Result values recorded by MEASURE and available to later IF instructions.", Items = new SwaggerSchema { Type = SwaggerSchemaType.Object } } },
                { "counts", new SwaggerSchema { Type = SwaggerSchemaType.Object, Title = "Measurement counts", Description = "Histogram of terminal measured bit strings." } },
                { "probabilities", new SwaggerSchema { Type = SwaggerSchemaType.Object, Title = "Empirical probabilities", Description = "Counts divided by shots for each measured bit string." } },
            });

        private ServiceOperationManifest BuildManifest(SwaggerSchema inputs, OrdinalDictionary<SwaggerSchema> outputs) => new ServiceOperationManifest
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
            Inputs = inputs,
            Outputs = new SwaggerSchema
            {
                Type = SwaggerSchemaType.Object,
                Properties = new OrdinalDictionary<SwaggerSchema>
                {
                    {
                        "body", new SwaggerSchema
                        {
                            Type = SwaggerSchemaType.Object,
                            Title = "Result",
                            Properties = outputs,
                        }
                    },
                },
            },
            Connector = this.serviceOperationApi,
        };

        private static SwaggerSchema StringSchema(string title, string description) =>
            new SwaggerSchema { Type = SwaggerSchemaType.String, Title = title, Description = description };

        private static SwaggerSchema IntegerSchema(string title, string description) =>
            new SwaggerSchema { Type = SwaggerSchemaType.Integer, Title = title, Description = description };
    }
}
