namespace QuantumCircuit.Connector
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Quantum.Simulation.Core;
    using Microsoft.Quantum.Simulation.Simulators;
    using Newtonsoft.Json.Linq;
    using QuantumCircuit.QSharp;

    /// <summary>One gate instruction in the submitted circuit.</summary>
    public sealed class QuantumCircuitGate
    {
        public string Gate { get; set; }

        public int? Target { get; set; }

        public int? Control { get; set; }

        public int? Control2 { get; set; }

        public double? Angle { get; set; }
    }

    /// <summary>Measurement histogram from repeated executions of one complete circuit.</summary>
    public sealed class QuantumCircuitRunResult
    {
        public string InputState { get; set; }

        public string Sample { get; set; }

        public int QubitCount { get; set; }

        public int GateCount { get; set; }

        public int Shots { get; set; }

        public IDictionary<string, int> Counts { get; set; }

        public IList<bool[]> Measurements { get; set; }

        public IList<IDictionary<string, bool>> ClassicalMeasurements { get; set; }
    }

    /// <summary>Runs ordered gates, measurements, resets, and bounded measurement branches in one Q# invocation per shot.</summary>
    public static class QuantumCircuitExecutor
    {
        public const int MaxQubits = 20;

        public const int MaxGates = 256;

        public const int MaxMeasurements = 32;

        public const int MaxOperations = 32;

        public const int MaxShots = 1000;

        public static void ValidateCircuit(string initialState, JArray gateTokens, JArray operations = null) => CompileCircuit(initialState, gateTokens, operations);

        private static (long[] Codes, long[] Targets, long[] Controls, long[] Controls2, double[] Angles, long[] MeasurementSlots, long[] ConditionSlots, bool[] ConditionValues, string[] MeasurementNames, int GateCount) CompileCircuit(string initialState, JArray gateTokens, JArray operations)
        {
            if (string.IsNullOrEmpty(initialState) || initialState.Any(c => c != '0' && c != '1'))
            {
                throw new ArgumentException("initialState must be a non-empty bit string containing only '0' and '1'.", nameof(initialState));
            }

            if (initialState.Length > MaxQubits)
            {
                throw new ArgumentOutOfRangeException(nameof(initialState), $"This circuit action supports at most {MaxQubits} qubits.");
            }

            if (gateTokens == null)
            {
                throw new ArgumentNullException(nameof(gateTokens), "gates must be a JSON array.");
            }

            gateTokens = ExpandOperationCalls(gateTokens, operations);
            var codes = new List<long>();
            var targets = new List<long>();
            var controls = new List<long>();
            var controls2 = new List<long>();
            var angles = new List<double>();
            var measurementSlots = new List<long>();
            var conditionSlots = new List<long>();
            var conditionValues = new List<bool>();
            var measurementNames = new List<string>();
            var measurementIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            var gateCount = 0;

            void AddInstruction(long code, long target, long control, long control2, double angle, long measurementSlot, long conditionSlot, bool conditionValue)
            {
                if (codes.Count >= MaxGates)
                {
                    throw new ArgumentOutOfRangeException(nameof(gateTokens), $"A circuit can contain at most {MaxGates} expanded instructions.");
                }

                codes.Add(code);
                targets.Add(target);
                controls.Add(control);
                controls2.Add(control2);
                angles.Add(angle);
                measurementSlots.Add(measurementSlot);
                conditionSlots.Add(conditionSlot);
                conditionValues.Add(conditionValue);
            }

            for (var i = 0; i < gateTokens.Count; i++)
            {
                if (gateTokens[i] is not JObject gateObject
                    || !gateObject.TryGetValue("gate", StringComparison.OrdinalIgnoreCase, out var nameToken)
                    || nameToken.Type != JTokenType.String)
                {
                    throw new ArgumentException($"gates[{i}] must be an object with a gate name.", nameof(gateTokens));
                }

                var name = nameToken.Value<string>()?.Trim().ToUpperInvariant();
                if (name == "MEASURE")
                {
                    var target = ReadIndex(gateObject, "target", i);
                    ValidateIndex(target, initialState.Length, i, "target");
                    if (!gateObject.TryGetValue("measurement", StringComparison.OrdinalIgnoreCase, out var measurementToken)
                        || measurementToken.Type != JTokenType.String)
                    {
                        throw new ArgumentException($"gates[{i}] MEASURE requires a named 'measurement' result.", nameof(gateTokens));
                    }

                    var measurementName = measurementToken.Value<string>();
                    if (!IsIdentifier(measurementName) || measurementIndices.ContainsKey(measurementName))
                    {
                        throw new ArgumentException($"gates[{i}] measurement name '{measurementName}' is invalid or duplicated.", nameof(gateTokens));
                    }
                    if (measurementNames.Count >= MaxMeasurements)
                    {
                        throw new ArgumentOutOfRangeException(nameof(gateTokens), $"A circuit can record at most {MaxMeasurements} named measurements.");
                    }

                    var slot = measurementNames.Count;
                    measurementNames.Add(measurementName);
                    measurementIndices.Add(measurementName, slot);
                    AddInstruction(15, target, -1, -1, 0, slot, -1, false);
                    continue;
                }

                if (name == "RESET")
                {
                    var target = ReadIndex(gateObject, "target", i);
                    ValidateIndex(target, initialState.Length, i, "target");
                    AddInstruction(16, target, -1, -1, 0, -1, -1, false);
                    continue;
                }

                if (name == "IF")
                {
                    if (!gateObject.TryGetValue("measurement", StringComparison.OrdinalIgnoreCase, out var referenceToken)
                        || referenceToken.Type != JTokenType.String
                        || !measurementIndices.TryGetValue(referenceToken.Value<string>(), out var conditionSlot))
                    {
                        throw new ArgumentException($"gates[{i}] IF must reference a named measurement produced earlier in the circuit.", nameof(gateTokens));
                    }
                    if (!gateObject.TryGetValue("equals", StringComparison.OrdinalIgnoreCase, out var equalsToken)
                        || equalsToken.Type != JTokenType.Boolean
                        || gateObject["then"] is not JArray thenGates)
                    {
                        throw new ArgumentException($"gates[{i}] IF requires a Boolean 'equals' and a 'then' gate array.", nameof(gateTokens));
                    }

                    var elseGates = gateObject["else"] == null ? new JArray() : gateObject["else"] as JArray
                        ?? throw new ArgumentException($"gates[{i}] IF 'else' must be a gate array.", nameof(gateTokens));
                    var expected = equalsToken.Value<bool>();
                    var expandedThen = ExpandOperationCalls(thenGates, operations);
                    var expandedElse = ExpandOperationCalls(elseGates, operations);
                    var branchGateCount = AppendConditionalGates(expandedThen, i, initialState.Length, conditionSlot, expected, AddInstruction);
                    branchGateCount += AppendConditionalGates(expandedElse, i, initialState.Length, conditionSlot, !expected, AddInstruction);
                    if (branchGateCount == 0)
                    {
                        throw new ArgumentException($"gates[{i}] IF must contain at least one gate in 'then' or 'else'.", nameof(gateTokens));
                    }
                    gateCount += branchGateCount;
                    continue;
                }

                AppendPrimitiveGate(gateObject, i, initialState.Length, -1, false, AddInstruction);
                gateCount++;
            }

            return (codes.ToArray(), targets.ToArray(), controls.ToArray(), controls2.ToArray(), angles.ToArray(),
                measurementSlots.ToArray(), conditionSlots.ToArray(), conditionValues.ToArray(), measurementNames.ToArray(), gateCount);
        }

        private static int AppendConditionalGates(JArray gates, int instructionIndex, int qubitCount, int conditionSlot, bool expected,
            Action<long, long, long, long, double, long, long, bool> addInstruction)
        {
            var count = 0;
            foreach (var token in gates)
            {
                if (token is not JObject gate)
                {
                    throw new ArgumentException($"gates[{instructionIndex}] IF branches may contain only primitive gate objects or reusable calls.", "gates");
                }

                AppendPrimitiveGate(gate, instructionIndex, qubitCount, conditionSlot, expected, addInstruction);
                count++;
            }
            return count;
        }

        private static void AppendPrimitiveGate(JObject gateObject, int gateIndex, int qubitCount, int conditionSlot, bool conditionValue,
            Action<long, long, long, long, double, long, long, bool> addInstruction)
        {
            if (!gateObject.TryGetValue("gate", StringComparison.OrdinalIgnoreCase, out var nameToken)
                || nameToken.Type != JTokenType.String)
            {
                throw new ArgumentException($"gates[{gateIndex}] must be an object with a gate name.", "gates");
            }

            var name = nameToken.Value<string>()?.Trim().ToUpperInvariant();
            var code = GetGateCode(name, gateIndex);
            if (code >= 15)
            {
                throw new ArgumentException($"gates[{gateIndex}] conditional branches may contain unitary gates only; measurements, resets, and nested IF instructions are not supported.", "gates");
            }
            var target = ReadIndex(gateObject, "target", gateIndex);
            ValidateIndex(target, qubitCount, gateIndex, "target");
            long control = -1;
            long control2 = -1;
            var angle = 0.0;

            if (code == 8 || code == 9 || code == 10 || code == 11)
            {
                control = ReadIndex(gateObject, "control", gateIndex);
                ValidateIndex((int)control, qubitCount, gateIndex, "control");
                if (control == target)
                {
                    throw new ArgumentException($"gates[{gateIndex}] control and target must be different qubits.", "gates");
                }
            }

            if (code == 11)
            {
                control2 = ReadIndex(gateObject, "control2", gateIndex);
                ValidateIndex((int)control2, qubitCount, gateIndex, "control2");
                if (control2 == target || control2 == control)
                {
                    throw new ArgumentException($"gates[{gateIndex}] Toffoli controls and target must be different qubits.", "gates");
                }
            }

            if (code >= 12)
            {
                if (!gateObject.TryGetValue("angle", StringComparison.OrdinalIgnoreCase, out var angleToken)
                    || angleToken.Type == JTokenType.Null
                    || !double.TryParse(
                        angleToken is JValue value ? Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture) : angleToken.ToString(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out angle)
                    || double.IsNaN(angle)
                    || double.IsInfinity(angle))
                {
                    throw new ArgumentException($"gates[{gateIndex}] '{name}' requires a finite numeric angle in radians.", "gates");
                }
            }

            addInstruction(code, target, control, control2, angle, -1, conditionSlot, conditionValue);
        }

        public static Task<QuantumCircuitRunResult> RunAsync(string initialState, JArray gateTokens, int shots) =>
            RunAsync(initialState, gateTokens, null, shots);

        public static async Task<QuantumCircuitRunResult> RunAsync(string initialState, JArray gateTokens, JArray operations, int shots)
        {
            var circuit = CompileCircuit(initialState, gateTokens, operations);
            if (shots < 1 || shots > MaxShots)
            {
                throw new ArgumentOutOfRangeException(nameof(shots), $"shots must be between 1 and {MaxShots}.");
            }

            var initialQubits = new QArray<bool>(initialState.Select(c => c == '1'));
            var qGateCodes = new QArray<long>(circuit.Codes);
            var qTargets = new QArray<long>(circuit.Targets);
            var qControls = new QArray<long>(circuit.Controls);
            var qControls2 = new QArray<long>(circuit.Controls2);
            var qAngles = new QArray<double>(circuit.Angles);
            var qMeasurementSlots = new QArray<long>(circuit.MeasurementSlots);
            var qConditionSlots = new QArray<long>(circuit.ConditionSlots);
            var qConditionValues = new QArray<bool>(circuit.ConditionValues);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var measurements = new List<bool[]>(shots);
            var classicalMeasurements = new List<IDictionary<string, bool>>(shots);
            string lastSample = null;

            using (var simulator = new QuantumSimulator())
            {
                for (var shot = 0; shot < shots; shot++)
                {
                    IQArray<Result> results = await RunCircuit.Run(
                        simulator,
                        initialQubits,
                        qGateCodes,
                        qTargets,
                        qControls,
                        qControls2,
                        qAngles,
                        qMeasurementSlots,
                        qConditionSlots,
                        qConditionValues,
                        circuit.MeasurementNames.Length).ConfigureAwait(false);

                    var shotResults = results.Select(result => result == Result.One).ToArray();
                    var measurement = shotResults.Skip(circuit.MeasurementNames.Length).Take(initialState.Length).ToArray();
                    var classicalMeasurement = new Dictionary<string, bool>(StringComparer.Ordinal);
                    for (var index = 0; index < circuit.MeasurementNames.Length; index++)
                    {
                        classicalMeasurement.Add(circuit.MeasurementNames[index], shotResults[index]);
                    }

                    measurements.Add(measurement);
                    classicalMeasurements.Add(classicalMeasurement);
                    lastSample = string.Concat(measurement.Select(value => value ? '1' : '0'));
                    counts.TryGetValue(lastSample, out var count);
                    counts[lastSample] = count + 1;
                }
            }

            return new QuantumCircuitRunResult
            {
                InputState = initialState,
                Sample = lastSample,
                QubitCount = initialState.Length,
                GateCount = circuit.GateCount,
                Shots = shots,
                Counts = counts,
                Measurements = measurements,
                ClassicalMeasurements = classicalMeasurements,
            };
        }

        private sealed record ReusableOperation(string Name, Dictionary<string, string> ParameterTypes, JArray Gates);

        private static JArray ExpandOperationCalls(JArray gateTokens, JArray operationTokens)
        {
            var operations = ReadOperations(operationTokens);
            var expanded = new JArray();

            foreach (var token in gateTokens)
            {
                if (token is not JObject call
                    || !call.TryGetValue("gate", StringComparison.OrdinalIgnoreCase, out var gateName)
                    || gateName.Type != JTokenType.String
                    || !string.Equals(gateName.Value<string>(), "CALL", StringComparison.OrdinalIgnoreCase))
                {
                    expanded.Add(token?.DeepClone());
                    continue;
                }

                if (call["operation"]?.Type != JTokenType.String
                    || call["arguments"] is not JObject arguments)
                {
                    throw new ArgumentException("Operation calls require a string operation name and an arguments object.", "gates");
                }

                var name = call["operation"].Value<string>();
                if (!operations.TryGetValue(name, out var operation))
                {
                    throw new ArgumentException($"Reusable operation '{name}' is not defined.", "gates");
                }

                ValidateArguments(operation, arguments);
                foreach (var template in operation.Gates)
                {
                    var instruction = (JObject)template.DeepClone();
                    foreach (var field in new[] { "target", "control", "control2", "angle" })
                    {
                        if (instruction[field]?.Type != JTokenType.String)
                        {
                            continue;
                        }

                        var reference = instruction[field].Value<string>();
                        if (!reference.StartsWith("$", StringComparison.Ordinal)
                            || !arguments.TryGetValue(reference.Substring(1), StringComparison.Ordinal, out var argument))
                        {
                            throw new ArgumentException($"Operation '{name}' has an unresolved parameter reference '{reference}'.", "operations");
                        }

                        instruction[field] = argument.DeepClone();
                    }

                    expanded.Add(instruction);
                    if (expanded.Count > MaxGates)
                    {
                        throw new ArgumentOutOfRangeException("gates", $"Expanded circuits can contain at most {MaxGates} gates.");
                    }
                }
            }

            return expanded;
        }

        private static Dictionary<string, ReusableOperation> ReadOperations(JArray operationTokens)
        {
            var operations = new Dictionary<string, ReusableOperation>(StringComparer.Ordinal);
            if (operationTokens == null)
            {
                return operations;
            }

            if (operationTokens.Count > MaxOperations)
            {
                throw new ArgumentOutOfRangeException(nameof(operationTokens), $"A circuit can define at most {MaxOperations} reusable operations.");
            }

            foreach (var token in operationTokens)
            {
                if (token is not JObject definition
                    || definition["name"]?.Type != JTokenType.String
                    || definition["parameters"] is not JArray parameterTokens
                    || definition["gates"] is not JArray gates)
                {
                    throw new ArgumentException("Each reusable operation requires a name, parameters array, and gates array.", nameof(operationTokens));
                }

                var name = definition["name"].Value<string>();
                if (!IsIdentifier(name) || operations.ContainsKey(name))
                {
                    throw new ArgumentException($"Reusable operation name '{name}' is invalid or duplicated.", nameof(operationTokens));
                }

                if (parameterTokens.Count > 32 || gates.Count > MaxGates)
                {
                    throw new ArgumentOutOfRangeException(nameof(operationTokens), $"Operation '{name}' exceeds its parameter or gate limit.");
                }

                var parameterTypes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var parameter in parameterTokens)
                {
                    if (parameter is not JObject parameterObject
                        || parameterObject["name"]?.Type != JTokenType.String
                        || parameterObject["type"]?.Type != JTokenType.String)
                    {
                        throw new ArgumentException($"Operation '{name}' parameters require string name and type fields.", nameof(operationTokens));
                    }

                    var parameterName = parameterObject["name"].Value<string>();
                    var parameterType = parameterObject["type"].Value<string>();
                    if (!IsIdentifier(parameterName)
                        || (parameterType != "Int" && parameterType != "Double")
                        || !parameterTypes.TryAdd(parameterName, parameterType))
                    {
                        throw new ArgumentException($"Operation '{name}' has an invalid or duplicate parameter '{parameterName}' (supported types: Int, Double).", nameof(operationTokens));
                    }
                }

                for (var index = 0; index < gates.Count; index++)
                {
                    if (gates[index] is not JObject gate
                        || !gate.TryGetValue("gate", StringComparison.OrdinalIgnoreCase, out var gateName)
                        || gateName.Type != JTokenType.String)
                    {
                        throw new ArgumentException($"Operation '{name}' gates[{index}] must be a gate instruction.", nameof(operationTokens));
                    }

                    var code = GetGateCode(gateName.Value<string>()?.Trim().ToUpperInvariant(), index);
                    if (code >= 15)
                    {
                        throw new ArgumentException($"Reusable operation '{name}' templates may contain gates only; measurements, resets, and conditions belong in the circuit stream.", nameof(operationTokens));
                    }
                    ValidateTemplateOperand(gate, "target", parameterTypes, name, index, false);
                    if (code == 8 || code == 9 || code == 10 || code == 11)
                    {
                        ValidateTemplateOperand(gate, "control", parameterTypes, name, index, false);
                    }
                    if (code == 11)
                    {
                        ValidateTemplateOperand(gate, "control2", parameterTypes, name, index, false);
                    }
                    if (code >= 12)
                    {
                        ValidateTemplateOperand(gate, "angle", parameterTypes, name, index, true);
                    }
                }

                operations.Add(name, new ReusableOperation(name, parameterTypes, (JArray)gates.DeepClone()));
            }

            return operations;
        }

        private static void ValidateTemplateOperand(JObject gate, string field, Dictionary<string, string> parameterTypes, string operationName, int gateIndex, bool angle)
        {
            if (!gate.TryGetValue(field, StringComparison.OrdinalIgnoreCase, out var value) || value.Type == JTokenType.Null)
            {
                throw new ArgumentException($"Operation '{operationName}' gates[{gateIndex}] requires '{field}'.", "operations");
            }

            if (value.Type == JTokenType.String)
            {
                var reference = value.Value<string>();
                if (!reference.StartsWith("$", StringComparison.Ordinal)
                    || !parameterTypes.TryGetValue(reference.Substring(1), out var parameterType)
                    || (!angle && parameterType != "Int"))
                {
                    throw new ArgumentException($"Operation '{operationName}' gates[{gateIndex}] has an invalid {field} parameter reference '{reference}'.", "operations");
                }
                return;
            }

            if ((!angle && value.Type != JTokenType.Integer)
                || (angle && value.Type != JTokenType.Integer && value.Type != JTokenType.Float))
            {
                throw new ArgumentException($"Operation '{operationName}' gates[{gateIndex}] '{field}' must be a numeric literal or a compatible parameter reference.", "operations");
            }

            if (angle && (!double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var radians)
                || double.IsNaN(radians) || double.IsInfinity(radians)))
            {
                throw new ArgumentException($"Operation '{operationName}' gates[{gateIndex}] angle must be finite.", "operations");
            }
        }

        private static void ValidateArguments(ReusableOperation operation, JObject arguments)
        {
            if (arguments.Count != operation.ParameterTypes.Count)
            {
                throw new ArgumentException($"Operation '{operation.Name}' expects {operation.ParameterTypes.Count} arguments.", "arguments");
            }

            foreach (var parameter in operation.ParameterTypes)
            {
                if (!arguments.TryGetValue(parameter.Key, StringComparison.Ordinal, out var value) || value == null)
                {
                    throw new ArgumentException($"Operation '{operation.Name}' is missing argument '{parameter.Key}'.", "arguments");
                }

                if (parameter.Value == "Int")
                {
                    if (value.Type != JTokenType.Integer
                        || !int.TryParse(value.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
                    {
                        throw new ArgumentException($"Operation '{operation.Name}' argument '{parameter.Key}' must be a 32-bit integer.", "arguments");
                    }
                }
                else if ((value.Type != JTokenType.Integer && value.Type != JTokenType.Float)
                    || !double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
                    || double.IsNaN(number) || double.IsInfinity(number))
                {
                    throw new ArgumentException($"Operation '{operation.Name}' argument '{parameter.Key}' must be a finite Double.", "arguments");
                }
            }

            foreach (var argument in arguments.Properties())
            {
                if (!operation.ParameterTypes.ContainsKey(argument.Name))
                {
                    throw new ArgumentException($"Operation '{operation.Name}' does not declare argument '{argument.Name}'.", "arguments");
                }
            }
        }

        private static bool IsIdentifier(string value) =>
            !string.IsNullOrEmpty(value)
            && (char.IsLetter(value[0]) || value[0] == '_')
            && value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');

        private static int GetGateCode(string name, int index) => name switch
        {
            "H" => 0,
            "X" => 1,
            "Y" => 2,
            "Z" => 3,
            "S" => 4,
            "S_DAG" or "SDG" => 5,
            "T" => 6,
            "T_DAG" or "TDG" => 7,
            "CNOT" => 8,
            "CZ" => 9,
            "SWAP" => 10,
            "TOFFOLI" or "CCNOT" => 11,
            "RX" => 12,
            "RY" => 13,
            "RZ" => 14,
            "MEASURE" => 15,
            "RESET" => 16,
            _ => throw new ArgumentException($"gates[{index}] has unsupported gate '{name}'.", "gates"),
        };

        private static int ReadIndex(JObject gate, string propertyName, int gateIndex)
        {
            if (!gate.TryGetValue(propertyName, StringComparison.OrdinalIgnoreCase, out var token)
                || token.Type == JTokenType.Null
                || !int.TryParse(token.ToString(), out var value))
            {
                throw new ArgumentException($"gates[{gateIndex}] requires integer '{propertyName}'.", "gates");
            }

            return value;
        }

        private static void ValidateIndex(int index, int qubitCount, int gateIndex, string propertyName)
        {
            if (index < 0 || index >= qubitCount)
            {
                throw new ArgumentOutOfRangeException("gates", $"gates[{gateIndex}] {propertyName} index {index} is outside [0..{qubitCount - 1}].");
            }
        }
    }
}
