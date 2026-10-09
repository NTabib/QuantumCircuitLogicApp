namespace QuantumCircuit.Connector
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;
    using Microsoft.Azure.WebJobs.Description;
    using Microsoft.Azure.WebJobs.Host.Config;
    using Microsoft.Azure.Workflows.ServiceProviders.Abstractions;
    using Microsoft.Azure.Workflows.ServiceProviders.WebJobs.Abstractions.Providers;
    using Microsoft.WindowsAzure.ResourceStack.Common.Collections;
    using Microsoft.WindowsAzure.ResourceStack.Common.Swagger.Entities;
    using Newtonsoft.Json.Linq;

    [ServiceOperationsProvider(Id = ServiceId, Name = ServiceName)]
    public sealed class QuantumCircuitBuilderServiceOperationProvider : IServiceOperationsProvider
    {
        public const string ServiceName = "quantumCircuitBuilder";
        public const string ServiceId = "/serviceProviders/quantumCircuitBuilder";
        private const string Format = "instruction-list-v1";
        private const uint BrandColor = 0x5C2D91;
        private static readonly Uri IconUri = new Uri("https://raw.githubusercontent.com/microsoft/qsharp/main/vscode/resources/qsharp.png");

        private sealed record GateAction(string Id, string Label, string Gate, bool HasControl = false, bool HasControl2 = false, bool HasAngle = false);

        private static readonly GateAction[] GateActions =
        {
            new("addHadamard", "Add Hadamard (H)", "H"),
            new("addX", "Add Pauli X", "X"),
            new("addY", "Add Pauli Y", "Y"),
            new("addZ", "Add Pauli Z", "Z"),
            new("addS", "Add phase S", "S"),
            new("addSAdjoint", "Add adjoint S", "S_DAG"),
            new("addT", "Add phase T", "T"),
            new("addTAdjoint", "Add adjoint T", "T_DAG"),
            new("addCnot", "Add CNOT", "CNOT", HasControl: true),
            new("addCz", "Add controlled Z", "CZ", HasControl: true),
            new("addSwap", "Add SWAP", "SWAP", HasControl: true),
            new("addToffoli", "Add Toffoli", "TOFFOLI", HasControl: true, HasControl2: true),
            new("addRx", "Add rotation Rx", "RX", HasAngle: true),
            new("addRy", "Add rotation Ry", "RY", HasAngle: true),
            new("addRz", "Add rotation Rz", "RZ", HasAngle: true),
        };

        private readonly ServiceOperationApi api;
        private readonly List<ServiceOperation> operations = new List<ServiceOperation>();
        private readonly List<ServiceOperation> expandedOperations = new List<ServiceOperation>();

        public QuantumCircuitBuilderServiceOperationProvider()
        {
            this.api = new ServiceOperationApi
            {
                Name = ServiceName,
                Id = ServiceId,
                Type = DesignerApiType.ServiceProvider,
                Properties = new ServiceOperationApiProperties
                {
                    DisplayName = "Quantum Circuit Builder",
                    Description = "Build quantum algorithms as visual gate steps, then execute the complete circuit once with the Q# executor.",
                    BrandColor = BrandColor,
                    IconUri = IconUri,
                    Capabilities = new[] { ApiCapability.Actions },
                    ConnectionParameters = new ConnectionParameters
                    {
                        AdditionalParameters = new InsensitiveDictionary<ConnectionStringParameters>
                        {
                            { "format", new ConnectionStringParameters
                                {
                                    Type = ConnectionStringType.StringType,
                                    ParameterSource = ConnectionParameterSource.AppConfiguration,
                                    UIDefinition = new UIDefinition
                                    {
                                        DisplayName = "Circuit format",
                                        Description = "Use instruction-list-v1.",
                                        Tooltip = Format,
                                        Constraints = new Constraints { Required = "true", Default = Format },
                                    },
                                }
                            },
                        },
                    },
                },
            };

            this.AddOperation("createCircuit", "Create circuit", new OrdinalDictionary<SwaggerSchema>
            {
                { "initialState", new SwaggerSchema
                    {
                        Type = SwaggerSchemaType.String,
                        Title = "Initial basis state",
                        Description = $"Bit string (e.g. 00). Left-most character is qubit 0; maximum {QuantumCircuitExecutor.MaxQubits} qubits.",
                    }
                },
            }, new[] { "initialState" });

            foreach (var gate in GateActions)
            {
                var inputs = new OrdinalDictionary<SwaggerSchema>
                {
                    { "circuit", CircuitSchema() },
                    { "target", IndexSchema("Target qubit") },
                };
                var required = new List<string> { "circuit", "target" };
                if (gate.HasControl)
                {
                    inputs.Add("control", IndexSchema(gate.Gate == "SWAP" ? "Other qubit" : "Control qubit"));
                    required.Add("control");
                }
                if (gate.HasControl2)
                {
                    inputs.Add("control2", IndexSchema("Second control qubit"));
                    required.Add("control2");
                }
                if (gate.HasAngle)
                {
                    inputs.Add("angle", new SwaggerSchema { Type = SwaggerSchemaType.Number, Title = "Angle (radians)", Description = "Finite rotation angle in radians." });
                    required.Add("angle");
                }
                this.AddOperation(gate.Id, gate.Label, inputs, required.ToArray());
            }
        }

        public ServiceOperationApi GetService() => this.api;

        public IEnumerable<ServiceOperation> GetOperations(bool expandManifest) => expandManifest ? this.expandedOperations : this.operations;

        public string GetBindingConnectionInformation(string operationId, InsensitiveDictionary<JToken> connectionParameters) => null;

        public Task<ServiceOperationResponse> InvokeOperation(string operationId, InsensitiveDictionary<JToken> connectionParameters, ServiceOperationRequest serviceOperationRequest)
        {
            if (connectionParameters == null || !connectionParameters.TryGetValue("format", out var format) || format?.ToString() != Format)
            {
                throw new ServiceOperationsProviderException(HttpStatusCode.BadRequest, ServiceOperationsErrorResponseCode.InvalidServiceProviderConnection, $"Connection format must be '{Format}'.", null);
            }

            var parameters = serviceOperationRequest.Parameters ?? new InsensitiveDictionary<JToken>();
            try
            {
                JObject circuit;
                if (string.Equals(operationId, "createCircuit", StringComparison.OrdinalIgnoreCase))
                {
                    var initialState = Required(parameters, "initialState");
                    if (initialState.Type != JTokenType.String)
                    {
                        throw new ArgumentException("initialState must be a bit string.");
                    }
                    QuantumCircuitExecutor.ValidateCircuit(initialState.Value<string>(), new JArray());
                    circuit = new JObject { ["initialState"] = initialState.DeepClone(), ["gates"] = new JArray() };
                }
                else
                {
                    var gate = GateActions.FirstOrDefault(action => string.Equals(action.Id, operationId, StringComparison.OrdinalIgnoreCase));
                    if (gate == null)
                    {
                        throw new ServiceOperationsProviderException(HttpStatusCode.BadRequest, ServiceOperationsErrorResponseCode.ServiceOperationIdNotSupported, $"Unsupported builder operation '{operationId}'.", null);
                    }
                    if (Required(parameters, "circuit") is not JObject input
                        || input["initialState"]?.Type != JTokenType.String
                        || input["gates"] is not JArray inputGates)
                    {
                        throw new ArgumentException("circuit must contain initialState (bit string) and gates (array), as returned by the previous builder action.");
                    }
                    QuantumCircuitExecutor.ValidateCircuit(input["initialState"].Value<string>(), inputGates);

                    // Never mutate the input: retries produce the same gate list, not duplicate gates.
                    circuit = (JObject)input.DeepClone();
                    var instruction = new JObject { ["gate"] = gate.Gate, ["target"] = Integer(parameters, "target") };
                    if (gate.HasControl) instruction["control"] = Integer(parameters, "control");
                    if (gate.HasControl2) instruction["control2"] = Integer(parameters, "control2");
                    if (gate.HasAngle)
                    {
                        var angle = Required(parameters, "angle");
                        if (angle.Type != JTokenType.Integer && angle.Type != JTokenType.Float)
                        {
                            throw new ArgumentException("angle must be a number in radians.");
                        }
                        instruction["angle"] = angle.DeepClone();
                    }
                    ((JArray)circuit["gates"]).Add(instruction);
                    QuantumCircuitExecutor.ValidateCircuit(circuit["initialState"].Value<string>(), (JArray)circuit["gates"]);
                }
                return Task.FromResult(new ServiceOperationResponse(new JObject { ["circuit"] = circuit }, HttpStatusCode.OK));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                throw new ServiceOperationsProviderException(HttpStatusCode.BadRequest, ServiceOperationsErrorResponseCode.ServiceOperationInvalidParameter, ex.Message, ex);
            }
        }

        private static JToken Required(InsensitiveDictionary<JToken> parameters, string name)
        {
            if (!parameters.TryGetValue(name, out var value) || value == null || value.Type == JTokenType.Null)
            {
                throw new ArgumentException($"Required parameter '{name}' is missing.");
            }
            return value;
        }

        private static JToken Integer(InsensitiveDictionary<JToken> parameters, string name)
        {
            var value = Required(parameters, name);
            if (value.Type != JTokenType.Integer)
            {
                throw new ArgumentException($"'{name}' must be an integer qubit index.");
            }
            return value.DeepClone();
        }

        private static SwaggerSchema IndexSchema(string title) => new SwaggerSchema { Type = SwaggerSchemaType.Integer, Title = title, Description = "Zero-based qubit index." };

        private static SwaggerSchema CircuitSchema() => new SwaggerSchema
        {
            Type = SwaggerSchemaType.Object,
            Title = "Circuit",
            Description = "Use the circuit output of the preceding builder action.",
            Properties = new OrdinalDictionary<SwaggerSchema>
            {
                { "initialState", new SwaggerSchema { Type = SwaggerSchemaType.String, Title = "Initial basis state" } },
                { "gates", new SwaggerSchema
                    {
                        Type = SwaggerSchemaType.Array,
                        Title = "Gate instructions",
                        Items = new SwaggerSchema
                        {
                            Type = SwaggerSchemaType.Object,
                            Properties = new OrdinalDictionary<SwaggerSchema>
                            {
                                { "gate", new SwaggerSchema { Type = SwaggerSchemaType.String, Title = "Gate name" } },
                                { "target", IndexSchema("Target") },
                                { "control", IndexSchema("Control") },
                                { "control2", IndexSchema("Second control") },
                                { "angle", new SwaggerSchema { Type = SwaggerSchemaType.Number, Title = "Angle (radians)" } },
                            },
                            Required = new[] { "gate", "target" },
                        },
                    }
                },
            },
            Required = new[] { "initialState", "gates" },
        };

        private void AddOperation(string id, string label, OrdinalDictionary<SwaggerSchema> inputs, string[] required)
        {
            var operation = new ServiceOperation
            {
                Name = id,
                Id = id,
                Type = id,
                Properties = new ServiceOperationProperties
                {
                    Api = this.api.GetFlattenedApi(),
                    Summary = label,
                    Description = "Builds a circuit descriptor without executing or measuring qubits. Execute the final descriptor with Run a quantum circuit.",
                    Visibility = Visibility.Important,
                    OperationType = OperationType.ServiceProvider,
                    BrandColor = BrandColor,
                    IconUri = IconUri,
                },
            };
            this.operations.Add(operation);
            this.expandedOperations.Add(operation.CloneWithManifest(new ServiceOperationManifest
            {
                ConnectionReference = new ConnectionReferenceFormat { ReferenceKeyFormat = ConnectionReferenceKeyFormat.ServiceProvider },
                InputsLocation = new[] { InputsLocation.Inputs, InputsLocation.Parameters },
                Inputs = new SwaggerSchema { Type = SwaggerSchemaType.Object, Properties = inputs, Required = required },
                Outputs = new SwaggerSchema
                {
                    Type = SwaggerSchemaType.Object,
                    Properties = new OrdinalDictionary<SwaggerSchema>
                    {
                        { "body", new SwaggerSchema
                            {
                                Type = SwaggerSchemaType.Object,
                                Properties = new OrdinalDictionary<SwaggerSchema> { { "circuit", CircuitSchema() } },
                            }
                        },
                    },
                },
                Connector = this.api,
            }));
        }
    }

    [Extension("QuantumCircuitBuilderServiceProvider", configurationSection: "QuantumCircuitBuilderServiceProvider")]
    public sealed class QuantumCircuitBuilderServiceProvider : IExtensionConfigProvider
    {
        public QuantumCircuitBuilderServiceProvider(ServiceOperationsProvider registry, QuantumCircuitBuilderServiceOperationProvider provider)
        {
            registry.RegisterService(serviceOperationsProviderId: QuantumCircuitBuilderServiceOperationProvider.ServiceId, serviceName: QuantumCircuitBuilderServiceOperationProvider.ServiceName, serviceOperationsProviderInstance: provider);
        }

        public void Initialize(ExtensionConfigContext context)
        {
        }
    }
}
