using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Quantum.Simulation.Core;
using Microsoft.Quantum.Simulation.Simulators;
using Newtonsoft.Json.Linq;
using QuantumCircuit.Connector;
using QuantumCircuit.QSharp;

await AzureQuantumConnectionTests.RunAsync();
await AzureQuantumWorkspaceClientTests.RunAsync();
if (args.Contains("--azure-connection-only"))
{
    return;
}

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

var workflowPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", "..", "src", "QuantumCircuit.LogicApp", "Shor15Workflow", "workflow.json"));
var workflow = JObject.Parse(System.IO.File.ReadAllText(workflowPath));
var shorGates = (JArray)workflow["definition"]!["actions"]!["Order_finding"]!["actions"]!["Execute_circuit"]!["inputs"]!["parameters"]!["gates"]!;
foreach (var (offset, gateCount, controlBits, multiplier) in new[] { (2, 6, "10", 4), (8, 9, "01", 2) })
{
    var controlledMultiply = new JArray(shorGates.Skip(offset).Take(gateCount).Select(gate => gate.DeepClone()));
    for (var value = 0; value < 16; value++)
    {
        var input = controlBits + Convert.ToString(value, 2).PadLeft(4, '0');
        var output = await QuantumCircuitExecutor.RunAsync(input, controlledMultiply, 1);
        var expectedValue = value == 15 ? 15 : (value * multiplier) % 15;
        var expectedOutput = controlBits + Convert.ToString(expectedValue, 2).PadLeft(4, '0');
        if (output.Sample != expectedOutput)
        {
            throw new InvalidOperationException($"Controlled modular multiplication of {value} by {multiplier} yielded {output.Sample}, expected {expectedOutput}.");
        }
    }
}

var shor = await QuantumCircuitExecutor.RunAsync("000001", shorGates, 80);
var orbit = new[] { "0001", "0010", "0100", "1000" };
var phases = new[] { "00", "01", "10", "11" };
if (shor.GateCount != 25 || shor.QubitCount != 6 || shor.Measurements.Count != 80
    || shor.Counts.Keys.Any(key => key.Length != 6 || !phases.Contains(key.Substring(0, 2))
        || !orbit.Contains(key.Substring(2)))
    || phases.Any(phase => !shor.Counts.Keys.Any(key => key.StartsWith(phase, StringComparison.Ordinal))))
{
    throw new InvalidOperationException($"Shor's factoring-15 circuit produced an invalid phase or work register: {string.Join(',', shor.Counts.Select(entry => $"{entry.Key}:{entry.Value}"))}.");
}
Console.WriteLine("Shor 15 modular multiplication passed all 16 inputs for both controlled powers; all four phases observed.");

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
