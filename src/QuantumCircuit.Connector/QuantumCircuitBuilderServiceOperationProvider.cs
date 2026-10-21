namespace QuantumCircuit.Connector
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Text;
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

        private sealed record GateAction(string Id, string Label, string Gate, bool HasControl = false, bool HasControl2 = false, bool HasAngle = false, bool HasMeasurementName = false, string IconLabel = "");

        private static readonly GateAction[] GateActions =
        {
            new("addHadamard", "Add Hadamard (H)", "H", IconLabel: "H"),
            new("addX", "Add Pauli X", "X", IconLabel: "X"),
            new("addY", "Add Pauli Y", "Y", IconLabel: "Y"),
            new("addZ", "Add Pauli Z", "Z", IconLabel: "Z"),
            new("addS", "Add phase S", "S", IconLabel: "S"),
            new("addSAdjoint", "Add adjoint S", "S_DAG", IconLabel: "S*"),
            new("addT", "Add phase T", "T", IconLabel: "T"),
            new("addTAdjoint", "Add adjoint T", "T_DAG", IconLabel: "T*"),
            new("addCnot", "Add CNOT", "CNOT", HasControl: true, IconLabel: "CNOT"),
            new("addCz", "Add controlled Z", "CZ", HasControl: true, IconLabel: "CZ"),
            new("addSwap", "Add SWAP", "SWAP", HasControl: true, IconLabel: "SWAP"),
            new("addToffoli", "Add Toffoli", "TOFFOLI", HasControl: true, HasControl2: true, IconLabel: "CCX"),
            new("addRx", "Add rotation Rx", "RX", HasAngle: true, IconLabel: "Rx"),
            new("addRy", "Add rotation Ry", "RY", HasAngle: true, IconLabel: "Ry"),
            new("addRz", "Add rotation Rz", "RZ", HasAngle: true, IconLabel: "Rz"),
            new("addMeasurement", "Measure qubit", "MEASURE", HasMeasurementName: true, IconLabel: "M"),
            new("addReset", "Reset qubit", "RESET", IconLabel: "R"),
        };

        private static readonly Uri CircuitIconUri = CreateIconUri(
            "<path d=\"M6 9h20M6 16h20M6 23h20\" stroke=\"#5C2D91\" stroke-width=\"2\"/><circle cx=\"11\" cy=\"9\" r=\"3\" fill=\"#5C2D91\"/><circle cx=\"21\" cy=\"16\" r=\"3\" fill=\"#5C2D91\"/>");

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
                    Description = "Build visual circuit steps for gates, named measurements, resets, reusable operations, and measurement conditions; execute once with Q#.",
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
            }, new[] { "initialState" }, CircuitIconUri);

            this.AddOperation("defineOperation", "Define reusable operation", new OrdinalDictionary<SwaggerSchema>
            {
                { "circuit", CircuitSchema() },
                { "name", new SwaggerSchema { Type = SwaggerSchemaType.String, Title = "Operation name", Description = "Identifier used by later Call operation steps." } },
                { "parameters", new SwaggerSchema
                    {
                        Type = SwaggerSchemaType.Array,
                        Title = "Typed parameters",
                        Description = "Parameter declarations currently support Int (qubit indices) and Double (rotation angles).",
                        Items = new SwaggerSchema { Type = SwaggerSchemaType.Object, Description = "Object with string name and type fields (Int or Double)." },
                    }
                },
                { "gates", new SwaggerSchema
                    {
                        Type = SwaggerSchemaType.Array,
                        Title = "Operation gate template",
                        Description = "Ordered gate instructions. Reference parameters with a whole-field string such as $target or $angle.",
                        Items = new SwaggerSchema { Type = SwaggerSchemaType.Object, Description = "Gate object; operands may be numeric literals or typed parameter references." },
                    }
                },
            }, new[] { "circuit", "name", "parameters", "gates" }, CircuitIconUri);

            this.AddOperation("callOperation", "Call reusable operation", new OrdinalDictionary<SwaggerSchema>
            {
                { "circuit", CircuitSchema() },
                { "name", new SwaggerSchema { Type = SwaggerSchemaType.String, Title = "Operation name", Description = "Previously defined reusable operation." } },
                { "arguments", new SwaggerSchema { Type = SwaggerSchemaType.Object, Title = "Typed arguments", Description = "Object whose values match the operation's Int and Double parameter declarations." } },
            }, new[] { "circuit", "name", "arguments" }, CircuitIconUri);

            this.AddOperation("ifMeasurement", "If measurement result", new OrdinalDictionary<SwaggerSchema>
            {
                { "circuit", CircuitSchema() },
                { "measurement", new SwaggerSchema { Type = SwaggerSchemaType.String, Title = "Earlier measurement name" } },
                { "equals", new SwaggerSchema { Type = SwaggerSchemaType.Boolean, Title = "Measurement equals" } },
                { "thenGates", new SwaggerSchema { Type = SwaggerSchemaType.Array, Title = "If branch gates", Description = "Primitive gates or calls to gate-only reusable operations; nested conditionals are not supported.", Items = new SwaggerSchema { Type = SwaggerSchemaType.Object } } },
                { "elseGates", new SwaggerSchema { Type = SwaggerSchemaType.Array, Title = "Else branch gates", Description = "Optional primitive gates or reusable operation calls.", Items = new SwaggerSchema { Type = SwaggerSchemaType.Object } } },
            }, new[] { "circuit", "measurement", "equals", "thenGates" }, CircuitIconUri);

            foreach (var gate in GateActions)
            {
                var inputs = new OrdinalDictionary<SwaggerSchema>
                {
                    { "circuit", CircuitSchema() },
                    { "target", IndexSchema("Target qubit") },
                };
                var required = new List<string> { "circuit", "target" };
                if (gate.HasMeasurementName)
                {
                    inputs.Add("measurement", new SwaggerSchema { Type = SwaggerSchemaType.String, Title = "Measurement name", Description = "Unique identifier for this measurement; later conditions may reference it." });
                    required.Add("measurement");
                }
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
                this.AddOperation(gate.Id, gate.Label, inputs, required.ToArray(), CreateGateIconUri(gate));
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
                    QuantumCircuitExecutor.ValidateCircuit(initialState.Value<string>(), new JArray(), new JArray());
                    circuit = new JObject { ["initialState"] = initialState.DeepClone(), ["gates"] = new JArray(), ["operations"] = new JArray() };
                }
                 else if (string.Equals(operationId, "defineOperation", StringComparison.OrdinalIgnoreCase))
                {
                    var input = GetCircuitInput(parameters, "defineOperation");
                    var inputGates = input["gates"] as JArray ?? throw new ArgumentException("circuit.gates must be an array.");
                    circuit = (JObject)input.DeepClone();
                    var operationName = Required(parameters, "name");
                    if (operationName.Type != JTokenType.String)
                    {
                        throw new ArgumentException("name must be a string.", "name");
                    }

                    var operations = GetCircuitOperations(input);
                    operations.Add(new JObject
                    {
                        ["name"] = operationName.DeepClone(),
                        ["parameters"] = RequiredArray(parameters, "parameters").DeepClone(),
                        ["gates"] = RequiredArray(parameters, "gates").DeepClone(),
                    });
                    circuit["operations"] = operations;
                    QuantumCircuitExecutor.ValidateCircuit(input["initialState"].Value<string>(), inputGates, operations);
                }
                 else if (string.Equals(operationId, "callOperation", StringComparison.OrdinalIgnoreCase))
                {
                    var input = GetCircuitInput(parameters, "callOperation");
                    circuit = (JObject)input.DeepClone();
                    var operationName = Required(parameters, "name");
                    if (operationName.Type != JTokenType.String || Required(parameters, "arguments") is not JObject arguments)
                    {
                        throw new ArgumentException("name must be a string and arguments must be an object.");
                    }

                    var gates = (JArray)circuit["gates"];
                    gates.Add(new JObject { ["gate"] = "CALL", ["operation"] = operationName.DeepClone(), ["arguments"] = arguments.DeepClone() });
                    var operations = GetCircuitOperations(input);
                    circuit["operations"] = operations;
                    QuantumCircuitExecutor.ValidateCircuit(input["initialState"].Value<string>(), gates, operations);
                }
                 else if (string.Equals(operationId, "ifMeasurement", StringComparison.OrdinalIgnoreCase))
                {
                    var input = GetCircuitInput(parameters, "ifMeasurement");
                    var measurement = Required(parameters, "measurement");
                    var equals = Required(parameters, "equals");
                    if (measurement.Type != JTokenType.String || equals.Type != JTokenType.Boolean)
                    {
                        throw new ArgumentException("measurement must be a string and equals must be a Boolean.");
                    }
                    var thenGates = RequiredArray(parameters, "thenGates");
                    var elseGates = parameters.TryGetValue("elseGates", out var optionalElseGates) && optionalElseGates != null
                        ? optionalElseGates as JArray ?? throw new ArgumentException("'elseGates' must be an array.", "elseGates")
                        : new JArray();

                    circuit = (JObject)input.DeepClone();
                    var gates = (JArray)circuit["gates"];
                    gates.Add(new JObject
                    {
                        ["gate"] = "IF",
                        ["measurement"] = measurement.DeepClone(),
                        ["equals"] = equals.DeepClone(),
                        ["then"] = thenGates.DeepClone(),
                        ["else"] = elseGates.DeepClone(),
                    });
                    var inputOperations = GetCircuitOperations(input);
                    circuit["operations"] = inputOperations;
                    QuantumCircuitExecutor.ValidateCircuit(input["initialState"].Value<string>(), gates, inputOperations);
                }
                 else
                {
                    var gate = GateActions.FirstOrDefault(action => string.Equals(action.Id, operationId, StringComparison.OrdinalIgnoreCase));
                    if (gate == null)
                    {
                        throw new ServiceOperationsProviderException(HttpStatusCode.BadRequest, ServiceOperationsErrorResponseCode.ServiceOperationIdNotSupported, $"Unsupported builder operation '{operationId}'.", null);
                    }
                    var input = GetCircuitInput(parameters, operationId);
                    var inputGates = input["gates"] as JArray ?? throw new ArgumentException("circuit.gates must be an array.");
                    var inputOperations = GetCircuitOperations(input);
                    QuantumCircuitExecutor.ValidateCircuit(input["initialState"].Value<string>(), inputGates, inputOperations);

                    // Never mutate the input: retries produce the same gate list, not duplicate gates.
                    circuit = (JObject)input.DeepClone();
                    var instruction = new JObject { ["gate"] = gate.Gate, ["target"] = Integer(parameters, "target") };
                    if (gate.HasMeasurementName)
                    {
                        var measurement = Required(parameters, "measurement");
                        if (measurement.Type != JTokenType.String)
                        {
                            throw new ArgumentException("measurement must be a string identifier.", "measurement");
                        }
                        instruction["measurement"] = measurement.DeepClone();
                    }
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
                    circuit["operations"] = inputOperations;
                    QuantumCircuitExecutor.ValidateCircuit(circuit["initialState"].Value<string>(), (JArray)circuit["gates"], inputOperations);
                }
                return Task.FromResult(new ServiceOperationResponse(new JObject { ["circuit"] = circuit }, HttpStatusCode.OK));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                throw new ServiceOperationsProviderException(HttpStatusCode.BadRequest, ServiceOperationsErrorResponseCode.ServiceOperationInvalidParameter, ex.Message, ex);
            }
        }

        private static JArray RequiredArray(InsensitiveDictionary<JToken> parameters, string name)
        {
            if (Required(parameters, name) is not JArray array)
            {
                throw new ArgumentException($"'{name}' must be an array.", name);
            }

            return array;
        }

        private static JArray GetCircuitOperations(JObject circuit)
        {
            if (circuit["operations"] == null)
            {
                return new JArray();
            }

            if (circuit["operations"] is not JArray operations)
            {
                throw new ArgumentException("circuit.operations must be an array.", "circuit");
            }

            return (JArray)operations.DeepClone();
        }

        private static JObject GetCircuitInput(InsensitiveDictionary<JToken> parameters, string operationName)
        {
            if (parameters.TryGetValue("circuit", out var circuitToken)
                && circuitToken != null
                && circuitToken.Type != JTokenType.Null
                && circuitToken is JObject circuitInput)
            {
                if (circuitInput["initialState"]?.Type != JTokenType.String || circuitInput["gates"] is not JArray)
                {
                    throw new ArgumentException($"{operationName}: circuit must contain initialState and gates, as returned by a prior builder action.");
                }

                return circuitInput;
            }

            var initialStateToken = parameters.TryGetValue("initialState", out var directInitialState) && directInitialState != null && directInitialState.Type != JTokenType.Null ? directInitialState : null;
            var gatesToken = parameters.TryGetValue("gates", out var directGates) && directGates != null && directGates.Type != JTokenType.Null ? directGates : null;
            if (initialStateToken == null || gatesToken == null)
            {
                throw new ArgumentException($"{operationName}: provide either the prior 'circuit' output or both 'initialState' and 'gates'.");
            }

            if (initialStateToken.Type != JTokenType.String || gatesToken is not JArray)
            {
                throw new ArgumentException($"{operationName}: initialState must be a bit string and gates must be an array.");
            }

            var directCircuit = new JObject
            {
                ["initialState"] = initialStateToken.DeepClone(),
                ["gates"] = (JArray)directGates.DeepClone(),
                ["operations"] = parameters.TryGetValue("operations", out var operationsToken) && operationsToken != null && operationsToken.Type != JTokenType.Null ? operationsToken.DeepClone() : new JArray(),
            };
            return directCircuit;
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
            Description = "Optional when the action is chained from a previous builder step. If omitted, provide initialState and gates directly instead.",
            Properties = new OrdinalDictionary<SwaggerSchema>
            {
                { "initialState", new SwaggerSchema { Type = SwaggerSchemaType.String, Title = "Initial basis state" } },
                { "gates", new SwaggerSchema
                    {
                        Type = SwaggerSchemaType.Array,
                        Title = "Ordered circuit instructions",
                        Description = "The final circuit can contain gates, reusable calls, named measurements, resets, and bounded IF measurement branches.",
                        Items = new SwaggerSchema { Type = SwaggerSchemaType.Object, Description = "Gate, reusable call, MEASURE, RESET, or IF instruction." },
                    }
                },
                { "operations", OperationDefinitionsSchema() },
            },
            Required = Array.Empty<string>(),
        };

        private static SwaggerSchema OperationDefinitionsSchema() => new SwaggerSchema
        {
            Type = SwaggerSchemaType.Array,
            Title = "Reusable operations",
            Description = "Named gate templates with typed Int and Double parameters.",
            Items = new SwaggerSchema
            {
                Type = SwaggerSchemaType.Object,
                Properties = new OrdinalDictionary<SwaggerSchema>
                {
                    { "name", new SwaggerSchema { Type = SwaggerSchemaType.String, Title = "Operation name" } },
                    { "parameters", new SwaggerSchema
                        {
                            Type = SwaggerSchemaType.Array,
                            Title = "Typed parameters",
                            Items = new SwaggerSchema
                            {
                                Type = SwaggerSchemaType.Object,
                                Properties = new OrdinalDictionary<SwaggerSchema>
                                {
                                    { "name", new SwaggerSchema { Type = SwaggerSchemaType.String, Title = "Parameter name" } },
                                    { "type", new SwaggerSchema { Type = SwaggerSchemaType.String, Title = "Parameter type", Description = "Int or Double." } },
                                },
                            },
                        }
                    },
                    { "gates", new SwaggerSchema
                        {
                            Type = SwaggerSchemaType.Array,
                            Title = "Gate template",
                            Items = new SwaggerSchema { Type = SwaggerSchemaType.Object, Description = "Use whole-field references such as $target and $angle." },
                        }
                    },
                },
            },
        };

        private static Uri CreateGateIconUri(GateAction gate)
        {
            var artwork = gate.Gate switch
            {
                "CNOT" => "<path d=\"M6 16h20\" stroke=\"#5C2D91\" stroke-width=\"2\"/><circle cx=\"10\" cy=\"16\" r=\"3\" fill=\"#5C2D91\"/><circle cx=\"23\" cy=\"16\" r=\"6\" fill=\"#F3E8FF\" stroke=\"#5C2D91\" stroke-width=\"2\"/><path d=\"M23 12v8M19 16h8\" stroke=\"#5C2D91\" stroke-width=\"2\"/>",
                "CZ" => "<path d=\"M6 16h20\" stroke=\"#5C2D91\" stroke-width=\"2\"/><circle cx=\"10\" cy=\"16\" r=\"4\" fill=\"#5C2D91\"/><circle cx=\"23\" cy=\"16\" r=\"4\" fill=\"#5C2D91\"/>",
                "SWAP" => "<path d=\"M7 10h18l-4-4M25 22H7l4 4M9 22l14-12M23 22 9 10\" fill=\"none\" stroke=\"#5C2D91\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>",
                "TOFFOLI" => "<path d=\"M9 9v14M9 16h16\" fill=\"none\" stroke=\"#5C2D91\" stroke-width=\"2\"/><circle cx=\"9\" cy=\"9\" r=\"3\" fill=\"#5C2D91\"/><circle cx=\"9\" cy=\"23\" r=\"3\" fill=\"#5C2D91\"/><circle cx=\"24\" cy=\"16\" r=\"6\" fill=\"#F3E8FF\" stroke=\"#5C2D91\" stroke-width=\"2\"/><path d=\"M24 12v8M20 16h8\" stroke=\"#5C2D91\" stroke-width=\"2\"/>",
                _ => $"<text x=\"16\" y=\"22\" text-anchor=\"middle\" font-family=\"Segoe UI,Arial,sans-serif\" font-size=\"{(gate.IconLabel.Length > 2 ? 11 : 18)}\" font-weight=\"700\" fill=\"#5C2D91\">{gate.IconLabel}</text>",
            };

            return CreateIconUri(artwork);
        }

        private static Uri CreateIconUri(string artwork)
        {
            var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\"><rect x=\"1\" y=\"1\" width=\"30\" height=\"30\" rx=\"7\" fill=\"#F3E8FF\" stroke=\"#5C2D91\" stroke-width=\"1.5\"/>{artwork}</svg>";
            var encodedSvg = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
            return new Uri($"data:image/svg+xml;base64,{encodedSvg}", UriKind.Absolute);
        }

        private void AddOperation(string id, string label, OrdinalDictionary<SwaggerSchema> inputs, string[] required, Uri iconUri)
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
                    IconUri = iconUri,
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
