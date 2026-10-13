# Quantum Circuit – Custom built-in connector (Q#) for Azure Logic Apps Standard

This solution wraps the **Microsoft Quantum Development Kit (Q# SDK)** in a **custom built-in
(service provider) connector** for **Logic Apps Standard**, uses it from a stateful workflow and
deploys everything to Azure.

Read the [illustrated implementation article](docs/quantum-computing-on-a-canvas.html),
**Quantum Computing on a Canvas: Building a Visual Designer with Azure Logic Apps and Q#**.
It includes four original diagrams, available as SVG and PNG in `docs\images`.

```
QuantumCircuitLogicApp/
├─ global.json                         # pins .NET SDK 8 (needed by the Q# compiler)
├─ QuantumCircuitLogicApp.sln
├─ src/
│  ├─ QuantumCircuit.QSharp/           # Q# circuit dispatcher for the supported gate set
│  │  └─ QuantumCircuit.qs
│  ├─ QuantumCircuit.Connector/        # custom built-in connectors, loaded in-process
│  │  ├─ QuantumCircuitBuilderServiceOperationProvider.cs # gates + measure/reset/IF actions
│  │  ├─ QuantumCircuitServiceOperationProvider.cs        # generic runCircuit action
│  │  ├─ QuantumCircuitExecutor.cs                       # validates and executes full circuits
│  │  └─ QuantumCircuitStartup.cs                        # registers both providers
│  └─ QuantumCircuit.LogicApp/         # NuGet-based Logic Apps Standard project
│     ├─ host.json / connections.json / local.settings.json
│     ├─ XGateDemoWorkflow/workflow.json  # X built with generic createCircuit/addX/runCircuit
│     ├─ Lesson*Workflow/workflow.json     # beginner gate lessons with explanations
│     ├─ ReusableOperationWorkflow/workflow.json # typed subroutines and Boolean results
│     ├─ QuantumCircuitWorkflow/workflow.json
│     └─ VisualQuantumCircuitWorkflow/workflow.json # visual gate steps -> executor
└─ deploy/
   ├─ main.bicep
   └─ deploy.ps1                       # provision + build + zip deploy + smoke tests
```

## 1. Q# library: one complete-circuit operation

`QuantumCircuit.qs` exposes `RunCircuit`, which allocates and prepares a register, applies the ordered
instruction list, then measures only after the full circuit. The Q# dispatcher covers H, X, Y, Z,
S/S-dagger, T/T-dagger, CNOT, CZ, SWAP, Toffoli, and Rx/Ry/Rz. X is gate code 1 in that same dispatcher;
there is no separate Q# `ApplyXGate` operation or X-only simulator wrapper.

The project uses classic `Microsoft.Quantum.Sdk` (QDK 0.28). The Q# compiler generates a .NET
`RunCircuit` operation class at build time; the connector calls it on the full-state simulator.

## 2. The built-in connectors

The NuGet-based Logic Apps Standard host registers two service-provider connectors:

* **Quantum Circuit Builder** (`quantumCircuitBuilder`) exposes `createCircuit` and 15 `add...`
  actions, including `addX`. Each action has its own embedded SVG icon (gate symbol or controlled-gate
  diagram) in the Logic Apps designer. Every gate action validates and appends one classical
  instruction to an immutable circuit descriptor; it does not allocate, measure, or mutate a live qubit.
* **Quantum Circuit** (`quantumCircuit`) exposes `runCircuit`. `QuantumCircuitExecutor` validates the
  complete descriptor, maps gate instructions to Q# arguments, runs each shot, and aggregates counts.

The old JSON logical-qubit connector and standalone X-gate execution actions have been removed. They
passed measured basis-state descriptors between independent steps and could not preserve superposition
or entanglement. X now follows the same builder path as H, CNOT, rotations, and every other supported
gate: append an `X` instruction, then execute the finished circuit once.

| Connector / operation | Inputs | Result |
|---|---|---|
| `quantumCircuitBuilder / createCircuit` | `initialState` bit string | descriptor with `initialState` and empty `gates` |
| `quantumCircuitBuilder / addX` | preceding `circuit`, zero-based `target` | new descriptor with an X instruction appended |
| `quantumCircuitBuilder / addHadamard`, `addCnot`, other gate actions | preceding `circuit` and gate operands | new descriptor with the requested instruction appended |
| `quantumCircuit / runCircuit` | `initialState`, ordered `gates[]`, `shots` | sample, measurement `counts`, empirical `probabilities` |

The builder connection uses `/serviceProviders/quantumCircuitBuilder` with
`format=instruction-list-v1`. The execution connection uses `/serviceProviders/quantumCircuit` with
`simulator=FullState`, the only supported backend. Limits are 20 qubits, 256 gate instructions, and
1,000 shots. Invalid gates or operands fail before simulation and appear as failed workflow actions.

If `Execute_circuit` reports `MissingServiceProviderConnectionParameterValues`, ensure
`serviceProviderConnections.quantumCircuit.parameterValues` in `connections.json` is
`{"simulator":"FullState"}`, not `{}`. Logic Apps requires nonempty connection values even though
this executor needs no credentials. Redeploy the updated connector and `connections.json` together;
for a designer-created connection, set its Simulator field to `FullState`.

## 3. X gate as an ordinary visual gate

`XGateDemoWorkflow` is a focused regression/demo of the generic path, not a separate X implementation:

```
HTTP request (optional shots)
  -> Create circuit (initialState = "0")
  -> Add X (target = 0)
  -> Run circuit
  -> HTTP response (circuit, counts, xGateVerified)
```

The workflow passes the circuit-builder output from one action to the next. Its complete descriptor is
`{"initialState":"0","gates":[{"gate":"X","target":0}]}`. For every supported shot count, the
result is deterministically `1`; deployment smoke tests check the assembled instruction and measured
histogram. To apply X elsewhere, add the builder's **Add Pauli X** step to any algorithm and continue
chaining gate actions before the final `runCircuit` action.

The visual Bell workflow remains a separate example (`VisualQuantumCircuitWorkflow`): it builds
`H(0) -> CNOT(0,1)` and measures only after both gates have executed coherently.

## Beginner gate lessons

Six small stateful workflows introduce core gate behavior. Each shows the individual builder actions
in the Logic Apps designer, executes the complete circuit once per shot, and returns a `lesson`
explanation, the assembled `circuit`, and a measurement histogram in `circuitResult`.

| Workflow | Gate steps | What to notice |
|---|---|---|
| `LessonPauliXWorkflow` | X on `|0>` | A basis-state bit flip: every shot returns `1`. |
| `LessonHadamardWorkflow` | H on `|0>` | Superposition gives approximately equal counts for `0` and `1`. |
| `LessonPhaseInterferenceWorkflow` | H -> Z -> H on `|0>` | Z changes relative phase; the second H makes it observable as `1`. |
| `LessonCnotWorkflow` | CNOT, control qubit 0 -> target qubit 1, input `|10>` | A control in `1` flips the target, giving `11`. |
| `LessonBellStateWorkflow` | H on qubit 0 -> CNOT(0,1) | Entangled outcomes are correlated: only `00` or `11`. |
| `LessonRotationWorkflow` | Ry(pi/2) on `|0>` | Rotation angles use radians; pi/2 produces equal measurement probabilities. |

POST `{}` or `{"shots":200}` to a lesson's HTTP Request trigger; the shot count defaults to 100.
For example, with a local host on port 7071:

```powershell
$url = (Invoke-RestMethod -Method Post 'http://localhost:7071/runtime/webhooks/workflow/api/management/workflows/LessonPhaseInterferenceWorkflow/triggers/When_a_HTTP_request_is_received/listCallbackUrl?api-version=2020-05-01-preview').value
Invoke-RestMethod -Method Post -Uri $url -ContentType application/json -Body '{"shots":100}'
```

The workflow's `lesson` describes the idea, while `circuit.gates` shows each visual builder step
and `circuitResult.counts` shows the measurements. Open a lesson workflow in the Logic Apps designer
to trace the Create -> Add gate(s) -> Execute sequence. `deploy\deploy.ps1` runs deterministic and
probabilistic assertions for all six lessons through `Test-BeginnerGateWorkflows.ps1`.

## 4. Coherent circuit workflow (`QuantumCircuitWorkflow`)

For superposition and entanglement, send the complete circuit as data to **one** `runCircuit` action.
The connector builds Q# gate instructions and executes them before any measurement; Logic Apps only
receives classical measurement results after each complete shot. This avoids serializing a live qubit
between workflow actions.

Supported instructions: `H`, `X`, `Y`, `Z`, `S`, `S_DAG` (`SDG`), `T`, `T_DAG` (`TDG`), `CNOT`, `CZ`,
`SWAP`, `TOFFOLI` (`CCNOT`), `RX`, `RY`, and `RZ`. Rotations take an `angle` in radians. `target` is
required; controlled gates also take `control`, and Toffoli takes `control2`. Limits: 20 qubits, 256
gates, and 1,000 shots per action. The action requires the `FullState` simulator.

Example request to the `QuantumCircuitWorkflow` HTTP trigger:

```json
{
  "initialState": "00",
  "gates": [
    { "gate": "H", "target": 0 },
    { "gate": "CNOT", "control": 0, "target": 1 }
  ],
  "shots": 100
}
```

This prepares a Bell state in one Q# run. Measurements should be correlated: `00` and `11` appear,
with roughly half of the shots each (finite-shot sampling varies). The response includes a sample,
counts, and empirical probabilities. The workflow defaults to this Bell-state example when `gates`
is omitted.

## Reusable operations and typed results

The `ReusableOperationWorkflow` lesson demonstrates the first executable Q# subset extension. The
builder's **Define reusable operation** action stores a named gate template in the circuit descriptor;
**Call reusable operation** adds a call with typed arguments. This example defines
`RotateYByAngle(target: Int, angle: Double)` once and calls it for two qubits. Gate-template operands
can use whole-field references such as `"$target"` and `"$angle"`. Currently supported operation
parameter types are `Int` and finite `Double`; `Int` values used as qubit indices are range-checked
when the completed circuit is compiled.

The executor expands operation calls to primitive gates before one Q# run, so calls can be reused
without moving a live qubit through Logic Apps. Definitions cannot call other definitions yet.
Expanded circuits retain the 256-instruction limit; a descriptor may define up to 32 reusable
operations with up to 32 parameters each. Reusable definitions remain gate-only and cannot contain
measurement, reset or classical branches. The generic `QuantumCircuitWorkflow` accepts the same `operations` definitions
and `CALL` instructions directly in its request body, for manual translation without builder actions.

Example request (two calls to the same typed operation):

```json
{
  "initialState": "00",
  "operations": [{
    "name": "RotateYByAngle",
    "parameters": [{ "name": "target", "type": "Int" }, { "name": "angle", "type": "Double" }],
    "gates": [{ "gate": "RY", "target": "$target", "angle": "$angle" }]
  }],
  "gates": [
    { "gate": "CALL", "operation": "RotateYByAngle", "arguments": { "target": 0, "angle": 1.5707963267948966 } },
    { "gate": "CALL", "operation": "RotateYByAngle", "arguments": { "target": 1, "angle": 1.5707963267948966 } }
  ],
  "shots": 100
}
```

`runCircuit` returns terminal `measurements` as a Boolean array per shot and `classicalMeasurements`
with each named mid-circuit result. `false` maps to Q# `Result.Zero`; `true` maps to `Result.One`.
Existing sample strings, counts, and probabilities remain available.

## Mid-circuit measurement, reset, and conditions

The instruction stream now supports ordered `MEASURE`, `RESET`, and one-level `IF` blocks. A
measurement collapses the selected qubit using Q# `M` and stores the outcome under a unique identifier;
`RESET` returns its qubit to `|0>` without exposing a separate result. A later `IF` may compare an
already-recorded name to a Boolean and execute a `then` or optional `else` list of unitary gates (or
calls to gate-only reusable operations). All of this executes inside the same Q# shot, so Logic Apps
still passes only classical circuit descriptions and results.

```json
[
  { "gate": "MEASURE", "target": 0, "measurement": "readout" },
  { "gate": "RESET", "target": 0 },
  { "gate": "IF", "measurement": "readout", "equals": true,
    "then": [{ "gate": "X", "target": 1 }],
    "else": [{ "gate": "Z", "target": 1 }] }
]
```

Measurement names must be unique identifiers and conditions can reference only earlier measurements.
At most 32 named measurements and 256 expanded instructions are accepted. Conditional branches may
contain unitary gates or reusable calls only; nested conditions, measurement/reset inside a branch,
`MeasurementControlWorkflow` demonstrates both IF and ELSE paths. `TeleportationWorkflow` uses
these instructions for Bell-basis measurement and classical feed-forward corrections; an additional H
followed by terminal Z measurement checks that Bob received `|+>`. `tests\QuantumCircuit.ReferenceTests`
checks collapse, reset and feed-forward against independent native Q# reference operations.

## Visual quantum algorithms (`VisualQuantumCircuitWorkflow`)

The **Quantum Circuit Builder** built-in connector exposes **Create circuit**, 15 gate actions,
named measurement, reset, and an **If measurement result** action. Each action takes the `circuit`
object from the preceding step and returns an updated `body.circuit`; they only append validated
instructions and never allocate or execute qubits. The new actions have distinct Measure (`M`) and
Reset (`R`) designer icons.

The supplied workflow builds a Bell algorithm visually:

```text
HTTP request -> Create circuit -> Hadamard on qubit 0 -> CNOT from 0 to 1
             -> Execute complete circuit -> HTTP response
```

Open `VisualQuantumCircuitWorkflow` in the Logic Apps Standard designer. To add a gate, choose
**Built-in -> Quantum Circuit Builder**, select its gate action, then set **Circuit** to the preceding
step's Circuit output. Set its zero-based Target/Control indices (rotations also take Angle in
radians). Connect the next step to this new output. At the final **Quantum Circuit -> Run a quantum
circuit** action, bind **Initial basis state** and **Circuit gates** to the final descriptor's
`initialState` and `gates`. Keep execution after the last builder step and select `FullState` for the
executor connection.

For example, a gate step uses `@body('Create_circuit')?['circuit']`, and the supplied final action
uses `@body('CNOT_from_0_to_1')?['circuit']?['gates']`. The schema exposes the Circuit output and
its Initial basis state and Gate instructions fields as dynamic content.

Send `{}` or `{"initialState":"00","shots":200}` to the new workflow's HTTP trigger. The response
contains both the assembled circuit and its measurement histogram. An invalid initial state, qubit
index, duplicate control/target, angle, or oversized circuit fails the builder step before execution.
Limits are shared with the executor: 20 qubits, 256 instructions, and 1,000 execution shots.

### Teleportation example

`TeleportationWorkflow` teleports `|+>` from qubit 0 to Bob's qubit 2. Here is the corresponding
Q# program (the final Hadamard changes the measurement basis to verify the teleported `|+>` state):

```qsharp
namespace QuantumCircuit.QSharp {
    open Microsoft.Quantum.Intrinsic;
    open Microsoft.Quantum.Measurement;

    operation TeleportPlus() : Result[] {
        use q = Qubit[3];
        H(q[0]);
        H(q[1]);
        CNOT(q[1], q[2]);
        CNOT(q[0], q[1]);
        H(q[0]);
        let m0 = M(q[0]);
        let m1 = M(q[1]);
        Reset(q[0]);
        Reset(q[1]);
        if m1 == One { X(q[2]); }
        if m0 == One { Z(q[2]); }
        H(q[2]);
        let output = MResetZ(q[2]);
        return [m0, m1, output];
    }
}
```

The workflow builds this sequence as a visual circuit: `H(0) -> H(1) -> CNOT(1,2) -> CNOT(0,1) ->
H(0) -> MEASURE(m0,m1) -> RESET(0,1) -> IF(m1) X(2) -> IF(m0) Z(2) -> H(2) -> runCircuit`.
The entire program runs in one Q# invocation per shot. `classicalMeasurements` returns `m0` and `m1`;
terminal `measurements` include the two reset qubits and Bob's verification result. For a correct
teleportation of `|+>`, every shot ends in `000`. To see the teleported state in the computational basis
instead, omit the final `H(2)`; the last bit is then a 50/50 sample.

The connection in `connections.json` uses `/serviceProviders/quantumCircuitBuilder` with
`format=instruction-list-v1`. It requires no external service or credentials. Each append deep-clones
the input descriptor; retrying the same step with the same input produces the same circuit, and
branches cannot mutate each other's gate lists. For a single ordered algorithm, chain gate steps
sequentially; parallel branches are separate circuits and are not automatically merged. Retrying
**execution** can produce different samples because measurement is probabilistic.

All instructions run in order within one Q# invocation per shot. Mid-circuit measurements collapse
state and their classical results can control the bounded IF action; terminal measurements still
produce the existing sample strings and histograms. This is visual circuit construction, not a
persistent quantum session. The deployment script publishes the builder, executor, X demo, six
beginner gate lessons, reusable-operation and measurement/control-flow lessons, teleportation,
visual workflow, and ten original algorithm examples together; no additional Azure resources are required.

With the local host running on port 7071, invoke the visual workflow:

```powershell
$visualUrl = (Invoke-RestMethod -Method Post 'http://localhost:7071/runtime/webhooks/workflow/api/management/workflows/VisualQuantumCircuitWorkflow/triggers/When_a_HTTP_request_is_received/listCallbackUrl?api-version=2020-05-01-preview').value
Invoke-RestMethod -Method Post -Uri $visualUrl -ContentType application/json -Body '{"initialState":"00","shots":200}'
```

In Azure, retrieve this workflow's callback URL from its Request trigger in the designer. Keep the URL
secret because it contains a SAS signature.

## Algorithm demonstration workflows

`TeleportationWorkflow` is documented above. The ten original algorithm workflows, a three-qubit
Grover example, and a fixed factoring-15 example are listed here. Each has its own `workflow.json`;
most demonstrations build circuits with individual designer gate actions, while Shor 15 supplies an
explicit 25-gate list to the Run circuit action. POST `{}` or `{"shots":100}` to a workflow's Request
trigger (1-1,000 shots per circuit). These are **small fixed instances**, not general-purpose
implementations accepting arbitrary oracles or problem sizes.

| Workflow | Demonstration and expected result |
|---|---|
| `MeasurementControlWorkflow` | Deterministic measurement/reset and conditional-branch lesson; every shot ends in `00`. |
| `TeleportationWorkflow` | Teleports `|+>` using Bell measurement and X/Z feed-forward, then verifies in the X basis; every shot ends in `000`. |
| `DeutschJozsaWorkflow` | Two input bits and an ancilla; balanced parity oracle `x0 XOR x1`. Input measurements are `11`; reports `balanced`. |
| `BernsteinVaziraniWorkflow` | Three-bit secret `101` encoded with CNOTs and phase kickback. Reports recovered secret `101`; final ancilla bit varies. |
| `GroverWorkflow` | Two-qubit search, marked item `11`, one oracle/diffusion iteration. Measures `11`. |
| `GroverThreeQubitWorkflow` | Starts at `000`, searches eight states for `111` with one oracle/diffusion iteration. Ideal probability of `111` is 25/32 (78.125%); each other state has probability 1/32 (3.125%). |
| `QuantumFourierTransformWorkflow` | Two-qubit QFT on `01`, with a second independently prepared QFT/inverse-QFT circuit. Forward Z measurements are uniform in expectation; round trip measures `01`. Controlled phases are decomposed into Rz and CNOT gates. |
| `QuantumPhaseEstimationWorkflow` | Two counting qubits estimate the eigenphase of S on `|1>`. Reports phase `0.25` and counting bits `01`; last bit is the eigenstate qubit. |
| `Shor15Workflow` | Fixed Shor order finding for `N=15`, `a=2`: six qubits, 25 gates, and inverse QFT. Informative phase `01` or `11` gives order 4 and factors `[3,5]`; otherwise reports `inconclusive`. |
| `SimonWorkflow` | Two-input oracle `f(x)=x0 XOR x1`, period `11`. Measures input strings `00` or `11`; an observed `11` recovers the period for this two-bit instance. Reports `inconclusive` if finite sampling gives no informative equation. This is not a general GF(2) solver. |
| `SuperdenseCodingWorkflow` | Creates a Bell pair, encodes fixed message `11`, and decodes it. Reports `11`. |
| `GhzWorkflow` | Three-qubit GHZ preparation. Measures only `000` or `111`. |
| `VqeWorkflow` | Single-qubit Hamiltonian Z and ansatz Ry(theta). Sequential classical sweep of theta = 0, pi/2, pi; computes `(count(0)-count(1))/shots` and minimizes it. Includes the exact ground energy `-1`. |
| `QaoaWorkflow` | Depth-one, two-vertex MaxCut. Sequential sweep of three (gamma,beta) pairs; cost is CNOT-Rz(-gamma)-CNOT, mixer is Rx(2 beta) on both qubits. Maximizes measured cut probability and includes parameters achieving `1`. |

VQE and QAOA return `evaluations`, `bestParameters`, `bestObjective`, and `bestCounts`; every iteration
builds a fresh circuit and measures it before classical comparison. Their sweeps are deliberately
small demonstrations rather than full numerical optimizers. Measurement noise can affect which
candidate wins a tie. The ten original algorithm demonstrations use computational-basis initialization
and final-only measurement; general teleportation, adaptive error correction, and scalable Shor
factoring are outside their scope. The separate measurement-control and teleportation workflows
exercise the mid-circuit subset.

### Three-qubit Grover search

`GroverThreeQubitWorkflow` chains 21 visible builder gate actions: H on qubits 0-2 creates an
equal superposition of eight states; H(2)-Toffoli(0,1,2)-H(2) marks `111` with a phase flip;
then H on all qubits, X on all qubits, the same three-gate controlled phase, X on all qubits,
and H on all qubits implement diffusion (up to an irrelevant global phase). The entire circuit
executes coherently in one Q# call per shot; measurement happens at the end. POST `{"shots":100}`
to its Request callback; the response includes `markedState: "111"`, the constructed `circuit`,
and `circuitResult.counts` and `circuitResult.probabilities`. With one Grover iteration, the
**ideal** probabilities are 78.125% for `111` and 3.125% for each of the other seven states.
Shot counts fluctuate, so the measured result is not guaranteed to be `111` on every run;
zero-count states may be omitted from the result. The marked item and iteration count are fixed
for this educational example. The original two-qubit `GroverWorkflow` remains unchanged.

### Factoring 15 with Shor's order-finding circuit

`Shor15Workflow` fixes `N=15` and coprime base `a=2`; its HTTP request only accepts optional
`shots` (default 100). The six-qubit initial state is `000001`: counting qubits 0 and 1 start at
zero and work qubits 2-5 encode the integer 1 (most-significant bit first). Hadamards prepare the
counting register. Controlled `U^2` on qubit 0 and controlled `U` on qubit 1 implement
`U|x> = |2x mod 15>` on the work register. For 4-bit inputs, `U` is a left bit rotation
(three swaps) and `U^2` rotates by two bits (two swaps); each controlled swap is decomposed as
`CNOT(a,b); TOFFOLI(control,b,a); CNOT(a,b)`. This is reversible for all 16 work states,
including 0 and 15, and is not a fabricated phase oracle. A two-qubit inverse QFT follows,
using SWAP, H, CNOT and Rz gates. All 25 gates execute coherently in one FullState simulator shot.

The first two measured bits are the estimated phase `00`, `01`, `10`, or `11` (numerator over 4);
the remaining four bits are the work register. The workflow filters the *per-shot* Boolean
measurements for `01` or `11`: continued-fraction denominators yield order `r=4`, verified here
by `2^4 mod 15 = 1` (and `2^2 mod 15 != 1`). Fixed classical post-processing then gives
`gcd(2^(r/2)-1,15)=3` and `gcd(2^(r/2)+1,15)=5`. Only when an informative phase occurs does
the response report `status: factored`, `order: 4`, `factors: [3,5]`; otherwise it reports
`status: inconclusive`, `order: 0`, `factors: []`. `informativeShots` and `observedPhaseBits`
show the evidence; `circuitResult` contains every raw outcome. The success chance per shot is
1/2, so the chance of no informative result after `shots` independent runs is `(1/2)^shots`.
The circuit, modular arithmetic, order, and gcd results are **specialized to 15 and 2**; this
is a genuine small order-finding demonstration, not a general-purpose Shor compiler, scalable
modular exponentiator, or arbitrary-number factorizer.

For example, POST `{"shots":100}` to the `Shor15Workflow` Request callback. When at least one
informative phase is observed, the response includes `{"algorithm":"Shor15","modulus":15,
"base":2,"status":"factored","order":4,"factors":[3,5]}` alongside the actual
`informativeShots`, `observedPhaseBits`, and `circuitResult`. With one uninformative shot, it
instead returns `{"status":"inconclusive","order":0,"factors":[]}`.

`deploy\Test-AlgorithmWorkflows.ps1` supplies algorithm-specific assertions. `deploy.ps1` runs them
after publishing, including QFT reversibility, phase recovery, Simon's finite-shot recovery rule,
and VQE/QAOA objectives recomputed from their measurements. `deploy\Test-BeginnerGateWorkflows.ps1`
checks the six introductory gate circuits, shot totals and allowed outcomes without requiring an exact
random 50/50 split. `deploy\Test-ReusableOperationWorkflow.ps1` checks typed reusable-operation calls,
Boolean measurement arrays and invalid-type rejection. `deploy\Test-MeasurementControlWorkflow.ps1`
checks measurement collapse, reset, true/else branches, the generic HTTP path and invalid forward
references. `deploy\Test-TeleportationWorkflow.ps1` checks Bell measurement, feed-forward corrections
and deterministic verification of the teleported `|+>` state. `deploy\Test-Shor15Workflow.ps1`
checks the factoring example's measured phase/work-register states and response against its raw
shot histogram. The algorithm suite also checks the 21-step Grover circuit and its measured
25/32 marked-state probability at 1,000 shots. Run `dotnet run --project
.\tests\QuantumCircuit.ReferenceTests -c Release` to compare executor shots with native Q# reference
programs, reject malformed control flow, and exercise the Shor circuit directly. All workflows
use the Q# full-state simulator.

To run these suites against a local host without provisioning Azure:

```powershell
. .\deploy\Test-AlgorithmWorkflows.ps1
. .\deploy\Test-BeginnerGateWorkflows.ps1
. .\deploy\Test-ReusableOperationWorkflow.ps1
. .\deploy\Test-MeasurementControlWorkflow.ps1
. .\deploy\Test-TeleportationWorkflow.ps1
. .\deploy\Test-Shor15Workflow.ps1
$resolver = { param($name) (Invoke-RestMethod -Method Post -Uri "http://localhost:7071/runtime/webhooks/workflow/api/management/workflows/$name/triggers/When_a_HTTP_request_is_received/listCallbackUrl?api-version=2020-05-01-preview").value }
Test-AlgorithmWorkflows -GetCallbackUrl $resolver
Test-AlgorithmWorkflows -GetCallbackUrl $resolver -OnlyWorkflow GroverThreeQubitWorkflow -Shots 1000
Test-BeginnerGateWorkflows -GetCallbackUrl $resolver
Test-ReusableOperationWorkflow -GetCallbackUrl $resolver
Test-MeasurementControlWorkflow -GetCallbackUrl $resolver
Test-TeleportationWorkflow -GetCallbackUrl $resolver
Test-Shor15Workflow -GetCallbackUrl $resolver
dotnet run --project .\tests\QuantumCircuit.ReferenceTests -c Release
python -m unittest discover -s .\tests\AzureQuantumBackendTests -v
```

## Azure Quantum hardware execution path

The existing `quantumCircuit/runCircuit` service provider remains the local Q# `FullState`
simulator. A separate `azureQuantumCircuit/runCircuit` operation and `AzureQuantumBellWorkflow`
use the same visual builder, then POST the completed circuit to a separate Python Azure Functions
backend. That backend selects a native circuit adapter from the configured target ID: Quil for Rigetti,
IonQ circuit JSON for IonQ, or OpenQASM 2.0 for Quantinuum. It waits for the Azure Quantum job and
returns the target, job ID, measured counts, probabilities, and shot count.
The new path does not replace or change any existing simulation workflow.

This deliberately runs the Azure Quantum submission service in a separate **Linux Python 3.11
Function App**; its cloud execution is independent of the classic Q# SDK used by the local simulator. The
backend source is `src\QuantumCircuit.AzureQuantumBackend`. Create a Function App with Functions v4,
configure its `AZURE_QUANTUM_WORKSPACE_RESOURCE_ID`, `AZURE_QUANTUM_TARGET_ID`, and
`AZURE_QUANTUM_JOB_TIMEOUT_SECONDS` app settings, then grant its system-assigned managed identity
the Azure Quantum workspace role that permits job submission and result reading (currently
**Quantum Workspace Data Contributor**). From the backend source directory, publish it with:

```powershell
func azure functionapp publish <backend-function-app-name> --python --build remote
```

The backend uses `DefaultAzureCredential`: locally, sign in with `az login`; in Azure, it uses the
Function App's managed identity. The deploy script currently assigns **Quantum Workspace Data
Contributor**; check the workspace's current Azure Quantum RBAC guidance and assign the equivalent
least-privilege job submit/read role if that role name is unavailable in your tenant. Select a target
available in that workspace and compatible with the circuit. This deployment defaults to Rigetti's
free cloud simulator, `rigetti.sim.qvm`. Supported targets are selected solely by
`AZURE_QUANTUM_TARGET_ID`: `rigetti.sim.qvm` or `rigetti.qpu.*` (native Quil),
`ionq.simulator` or `ionq.qpu.*` (IonQ circuit JSON), and `quantinuum.sim.*` or
`quantinuum.qpu.*` (OpenQASM 2.0). The SDK adapters use Azure's `rigetti.quil.v1`,
`ionq.circuit.v1`, and `honeywell.openqasm.v1` formats respectively. See the
[Rigetti](https://learn.microsoft.com/azure/quantum/provider-rigetti),
[IonQ](https://learn.microsoft.com/azure/quantum/provider-ionq), and
[Quantinuum](https://learn.microsoft.com/azure/quantum/provider-quantinuum) target documentation
to confirm the target IDs currently enabled in your workspace. Providers that require pulse/analog
programs, including Pasqal, are not supported by this gate-circuit backend.

To create and configure the separate backend and its managed identity, run
`deploy\Deploy-AzureQuantumBackend.ps1` after the Logic App, storage account, and Azure Quantum
workspace exist:

```powershell
.\deploy\Deploy-AzureQuantumBackend.ps1 `
  -ResourceGroup <resource-group> `
  -LogicAppName <logic-app-name> `
  -FunctionAppName <globally-unique-function-app-name> `
  -StorageAccountName <existing-storage-account-name> `
  -Location <function-app-region> `
  -WorkspaceResourceId <quantum-workspace-resource-id> `
  -TargetId rigetti.sim.qvm
```

Set `-TargetId` to an enabled supported target when selecting IonQ or Quantinuum. For an existing
Function App, changing `AZURE_QUANTUM_TARGET_ID` is sufficient; the backend code does not need to be
republished. The script grants the backend Function App identity the workspace data-contributor role,
publishes the Python app, and sets `AZURE_QUANTUM_BACKEND_URL` plus the function-scoped key as Logic App host
settings (not in source control). For production, replace the key setting with a Key Vault reference.
The function key protects the backend HTTP endpoint; the backend identity, not the Logic App, accesses
the Quantum workspace. `connections.json` registers the new service provider without embedding
credentials. Its `serviceProviderConnections.azureQuantumCircuit.parameterValues` must be
`{"backend":"AzureQuantum"}`, not `{}`: Logic Apps requires nonempty connection values even when
authentication is configured through host settings. For a designer-created connection, set
**Backend** to `AzureQuantum`. If `Run_on_Azure_Quantum` fails with
`MissingServiceProviderConnectionParameterValues`, redeploy the corrected Logic App connector
and `connections.json` together, or update the deployed connection values and save the workflow.
Redeploying only the Python Function App does not update the Logic App connection.
The existing `deploy\deploy.ps1` deliberately does not create a workspace or backend;
run this separate setup before calling `AzureQuantumBellWorkflow`. The Azure Quantum workspace and
provider must already be provisioned and the configured target must be enabled in that workspace.

The hardware path accepts computational-basis initial states, supported unitary gates (`H`, `X`,
`Y`, `Z`, `S`, `S_DAG`, `T`, `T_DAG`, `CNOT`, `CZ`, `SWAP`, `TOFFOLI`, `RX`, `RY`, `RZ`) and
terminal measurements. It explicitly rejects mid-circuit `MEASURE`, `RESET`, `IF`, and reusable
`CALL`; use the simulator path for those workflows. The adapters map these gate names to Quil, IonQ's
QIS circuit schema, and OpenQASM 2.0; target-specific gate/profile limits and workspace offerings
still apply. IonQ simulator histograms are exact distributions rather than sampled shots; the backend
converts their weights to an integer histogram summing to the requested shot count. The sample
workflow submits H(0), CNOT(0,1), so its ideal counts are split between `00` and `11`;
real QPU noise and finite shots affect the observed distribution. The backend waits synchronously
for up to 180 seconds, while the connector HTTP timeout is 210 seconds. A remote job may continue
after a synchronous timeout; automatic workflow retry is disabled to avoid accidental duplicate
submissions, so inspect the workspace job status before rerunning. Use a simulator target first:
QPU submissions may incur provider charges.

If publishing shows an empty `Functions in ...` list followed by `Error retrieving function keys`,
the Python host may have failed to index `function_app.py`. Check the backend's Application Insights
exceptions or Log stream for the underlying import error. The backend imports `azure.quantum` and its
core Rigetti, IonQ, and Quantinuum target adapters, installed through the `azure-quantum` package in
`requirements.txt`; `qdk.azure` is not the adapter namespace. Native submissions avoid the
QDK/Qiskit extra's Linux wheels, which require glibc 2.35 and cannot be installed by the
Debian Bullseye Oryx builder. No QDK or Qiskit runtime is required by this backend.
Republish from `src\QuantumCircuit.AzureQuantumBackend` with
`func azure functionapp publish <backend-function-app-name> --python --build remote`, or rerun
`deploy\Deploy-AzureQuantumBackend.ps1` with the same parameters to also configure the Logic App key.
The deployment script waits for discovery of `run_quantum_circuit` and its key before configuring
the Logic App; it fails with indexing guidance if the function remains unavailable.

If Core Tools reports `Failed to retrieve remote build status`, that is not confirmation of a
successful deployment. The deployment script uses Azure CLI ZIP deployment with `--build-remote true`
and a 20-minute deployment timeout, and packages only `function_app.py`, `host.json`, and
`requirements.txt` (not local Python packages or caches). It stops on a build failure before
requesting function keys and synchronizes the deployed function triggers before checking discovery.
Inspect the remote deployment and its build log with:

```powershell
az webapp log deployment list --resource-group <resource-group> --name <backend-function-app-name>
az webapp log deployment show --resource-group <resource-group> --name <backend-function-app-name> --deployment-id <deployment-id>
```

Deployment status `4` means success; `3` means failure; `0`, `1`, and `2` mean deployment has not
completed. If a build remains stuck at dependency installation, inspect the detailed Oryx log
linked from the deployment log before starting another deployment. SCM basic authentication is
not required to use the deployment script; do not enable it merely to bypass this status error.

## 5. Run locally

Prerequisites: .NET SDK 8 (+ .NET 6 runtime for the Q# compiler), Azure Functions Core Tools v4, Azurite.

```powershell
# terminal 1
azurite --location $env:TEMP\azurite
# terminal 2
dotnet publish src\QuantumCircuit.LogicApp -c Release -o artifacts\publish
Copy-Item src\QuantumCircuit.LogicApp\local.settings.json artifacts\publish\
cd artifacts\publish; func start
# terminal 3 – get the callback URL then call it
  $u = (Invoke-RestMethod -Method Post "http://localhost:7071/runtime/webhooks/workflow/api/management/workflows/QuantumCircuitWorkflow/triggers/When_a_HTTP_request_is_received/listCallbackUrl?api-version=2020-05-01-preview").value
Invoke-RestMethod -Method Post -Uri $u -ContentType application/json -Body '{"initialState":"00","gates":[{"gate":"H","target":0},{"gate":"CNOT","control":0,"target":1}],"shots":100}'
```

The host log shows `Loaded extension 'QuantumCircuit'`. To use the visual designer in VS Code, open
`src\QuantumCircuit.LogicApp` with the Azure Logic Apps (Standard) extension; the connector appears
under **Built-in → Quantum Circuit (Q#)**.

## 6. Deploy to Azure

```powershell
az login
.\deploy\deploy.ps1 -ResourceGroup rg-quantum-circuit -Location westeurope
```

What the script does:

1. **`main.bicep`** creates the storage account/file share, monitoring resources, Windows Workflow Standard plan, and Logic App Standard site with the .NET 8 in-process host settings.
2. **`dotnet publish`** compiles the Q# library, both built-in connectors, workflow definitions, and simulator dependencies into `artifacts\publish`.
3. **Zip deploy** publishes that package to the Logic App.
4. The script obtains workflow callback URLs and smoke-tests `XGateDemoWorkflow`, all six beginner gate lessons, the reusable-operation workflow, the visual Bell workflow, and all ten algorithm examples. It checks typed inputs/results, deterministic outcomes, and valid probabilistic measurement sets without requiring an exact 50/50 split.

### CI/CD

The same steps map to a pipeline: `az deployment group create` (Bicep) → `dotnet publish` →
`azure/functions-action` or `az logicapp deployment source config-zip`. Keep the callback URL in a
secret store (it contains a SAS signature).

## Notes & limitations

* **QDK version**: the local in-process .NET simulator uses the *classic* QDK
  (`Microsoft.Quantum.Sdk` 0.28, last release). The separate, optional Azure Quantum path uses the
  Python Azure Quantum SDK with native Quil, IonQ circuit JSON, or Quantinuum OpenQASM adapters; it
  does not replace the simulator or load the classic SDK in its
  Function App. Actual QPU execution depends on the selected workspace target, provider availability,
  circuit compatibility, and provider billing.
* The full-state simulator is limited to 20 qubits for `runCircuit` (state-vector memory grows
  exponentially); the application also caps circuits at 256 gates and 1,000 shots.
* The X gate on a basis state is deterministic, so `counts` is always 100 % on the flipped value. By
  contrast, general `runCircuit` sequences measure only after all gates and can return probabilistic samples.
