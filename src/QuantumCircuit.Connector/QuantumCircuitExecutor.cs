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
    }

    /// <summary>Runs all gates before measuring, so quantum state stays inside the Q# simulator.</summary>
    public static class QuantumCircuitExecutor
    {
        public const int MaxQubits = 20;

        public const int MaxGates = 256;

        public const int MaxShots = 1000;

        public static void ValidateCircuit(string initialState, JArray gateTokens) => CompileCircuit(initialState, gateTokens);

        private static (long[] Codes, long[] Targets, long[] Controls, long[] Controls2, double[] Angles) CompileCircuit(string initialState, JArray gateTokens)
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

            if (gateTokens.Count > MaxGates)
            {
                throw new ArgumentOutOfRangeException(nameof(gateTokens), $"A circuit can contain at most {MaxGates} gates.");
            }

            var codes = new long[gateTokens.Count];
            var targets = new long[gateTokens.Count];
            var controls = Enumerable.Repeat(-1L, gateTokens.Count).ToArray();
            var controls2 = Enumerable.Repeat(-1L, gateTokens.Count).ToArray();
            var angles = new double[gateTokens.Count];

            for (var i = 0; i < gateTokens.Count; i++)
            {
                if (gateTokens[i] is not JObject gateObject
                    || !gateObject.TryGetValue("gate", StringComparison.OrdinalIgnoreCase, out var nameToken))
                {
                    throw new ArgumentException($"gates[{i}] must be an object with a gate name.", nameof(gateTokens));
                }

                var name = nameToken.Value<string>()?.Trim().ToUpperInvariant();
                var code = GetGateCode(name, i);
                var target = ReadIndex(gateObject, "target", i);
                ValidateIndex(target, initialState.Length, i, "target");

                codes[i] = code;
                targets[i] = target;

                if (code == 8 || code == 9 || code == 10 || code == 11)
                {
                    controls[i] = ReadIndex(gateObject, "control", i);
                    ValidateIndex((int)controls[i], initialState.Length, i, "control");
                    if (controls[i] == target)
                    {
                        throw new ArgumentException($"gates[{i}] control and target must be different qubits.", nameof(gateTokens));
                    }
                }

                if (code == 11)
                {
                    controls2[i] = ReadIndex(gateObject, "control2", i);
                    ValidateIndex((int)controls2[i], initialState.Length, i, "control2");
                    if (controls2[i] == target || controls2[i] == controls[i])
                    {
                        throw new ArgumentException($"gates[{i}] Toffoli controls and target must be different qubits.", nameof(gateTokens));
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
                            out angles[i])
                        || double.IsNaN(angles[i])
                        || double.IsInfinity(angles[i]))
                    {
                        throw new ArgumentException($"gates[{i}] '{name}' requires a finite numeric angle in radians.", nameof(gateTokens));
                    }
                }
            }

            return (codes, targets, controls, controls2, angles);
        }

        public static async Task<QuantumCircuitRunResult> RunAsync(string initialState, JArray gateTokens, int shots)
        {
            var circuit = CompileCircuit(initialState, gateTokens);
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
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
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
                        qAngles).ConfigureAwait(false);

                    lastSample = string.Concat(results.Select(result => result == Result.One ? '1' : '0'));
                    counts.TryGetValue(lastSample, out var count);
                    counts[lastSample] = count + 1;
                }
            }

            return new QuantumCircuitRunResult
            {
                InputState = initialState,
                Sample = lastSample,
                QubitCount = initialState.Length,
                GateCount = gateTokens.Count,
                Shots = shots,
                Counts = counts,
            };
        }

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
