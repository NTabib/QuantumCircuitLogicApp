using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Quantum.Simulation.Core;
using Microsoft.Quantum.Simulation.Simulators;
using Newtonsoft.Json.Linq;
using QuantumCircuit.Connector;
using QuantumCircuit.QSharp;

var gates = JArray.Parse("""
[
  { "gate": "MEASURE", "target": 0, "measurement": "measuredZero" },
  { "gate": "X", "target": 0 },
  { "gate": "MEASURE", "target": 0, "measurement": "measuredOne" },
  { "gate": "RESET", "target": 0 },
  {
    "gate": "IF", "measurement": "measuredOne", "equals": true,
    "then": [{ "gate": "X", "target": 1 }],
    "else": [{ "gate": "X", "target": 0 }]
  },
  {
    "gate": "IF", "measurement": "measuredZero", "equals": true,
    "then": [{ "gate": "X", "target": 0 }],
    "else": [{ "gate": "X", "target": 1 }]
  }
]
""");

const int shots = 20;
var actual = await QuantumCircuitExecutor.RunAsync("00", gates, shots);
IQArray<Result> reference;
using (var simulator = new QuantumSimulator())
{
    reference = await ReferenceMeasurementResetControl.Run(simulator);
}

var expected = reference.Select(result => result == Result.One).ToArray();
if (expected.Length != 4 || !expected.SequenceEqual(new[] { false, true, false, false }))
{
    throw new InvalidOperationException("The independent Q# reference program returned unexpected results.");
}

if (actual.Shots != shots || actual.GateCount != 5 || actual.Measurements.Count != shots
    || actual.ClassicalMeasurements.Count != shots || actual.Counts.Count != 1 || !actual.Counts.TryGetValue("00", out var count) || count != shots)
{
    throw new InvalidOperationException($"Unexpected executor results: shots={actual.Shots}, gates={actual.GateCount}, measurements={actual.Measurements.Count}, classical={actual.ClassicalMeasurements.Count}, counts={string.Join(',', actual.Counts.Select(entry => $"{entry.Key}:{entry.Value}"))}.");
}

foreach (var shot in actual.ClassicalMeasurements.Zip(actual.Measurements))
{
    var actualResults = new[] { shot.First["measuredZero"], shot.First["measuredOne"], shot.Second[0], shot.Second[1] };
    if (!actualResults.SequenceEqual(expected))
    {
        throw new InvalidOperationException("The circuit executor disagrees with the independent Q# reference program.");
    }
}

var collapseInstructions = JArray.Parse("""
[
  { "gate": "H", "target": 0 },
  { "gate": "MEASURE", "target": 0, "measurement": "first" },
  { "gate": "MEASURE", "target": 0, "measurement": "repeated" },
  { "gate": "RESET", "target": 0 }
]
""");
var collapseResult = await QuantumCircuitExecutor.RunAsync("0", collapseInstructions, shots);
IQArray<Result> collapseReference;
using (var simulator = new QuantumSimulator())
{
    collapseReference = await ReferenceMeasurementCollapseAndReset.Run(simulator);
}
if (collapseReference[0] != collapseReference[1] || collapseReference[2] != Result.Zero
    || collapseResult.Measurements.Any(measurement => measurement.Length != 1 || measurement[0])
    || collapseResult.ClassicalMeasurements.Any(measurement => measurement["first"] != measurement["repeated"]))
{
    throw new InvalidOperationException("Repeated mid-circuit measurement or reset disagrees with the native Q# reference.");
}

AssertRejected(new JArray(JObject.Parse("{ 'gate':'IF', 'measurement':'later', 'equals':true, 'then':[{'gate':'X','target':0}] }")), "forward measurement reference");
AssertRejected(new JArray(JObject.Parse("{ 'gate':'MEASURE', 'target':0, 'measurement':'same' }"), JObject.Parse("{ 'gate':'MEASURE', 'target':1, 'measurement':'same' }")), "duplicate measurement name");
AssertRejected(new JArray(JObject.Parse("{ 'gate':'MEASURE', 'target':0, 'measurement':'m' }"), JObject.Parse("{ 'gate':'IF', 'measurement':'m', 'equals':true, 'then':[{'gate':'IF','measurement':'m','equals':true,'then':[{'gate':'X','target':0}]}] }")), "nested conditional");
Console.WriteLine($"Matched {shots} circuit shots against native Q# references for measurement, reset, and feed-forward; invalid control-flow cases rejected.");

static void AssertRejected(JArray instructions, string scenario)
{
    try
    {
        QuantumCircuitExecutor.ValidateCircuit("00", instructions);
    }
    catch (ArgumentException)
    {
        return;
    }

    throw new InvalidOperationException($"The executor accepted invalid {scenario}.");
}
