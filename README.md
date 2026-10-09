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
│  │  ├─ QuantumCircuitBuilderServiceOperationProvider.cs # createCircuit + 15 gate actions
│  │  ├─ QuantumCircuitServiceOperationProvider.cs        # generic runCircuit action
│  │  ├─ QuantumCircuitExecutor.cs                       # validates and executes full circuits
│  │  └─ QuantumCircuitStartup.cs                        # registers both providers
│  └─ QuantumCircuit.LogicApp/         # NuGet-based Logic Apps Standard project
│     ├─ host.json / connections.json / local.settings.json
│     ├─ XGateDemoWorkflow/workflow.json  # X built with generic createCircuit/addX/runCircuit
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
  actions, including `addX`. Every gate action validates and appends one classical instruction to an
  immutable circuit descriptor; it does not allocate, measure, or mutate a live qubit.
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

## Visual quantum algorithms (`VisualQuantumCircuitWorkflow`)

The **Quantum Circuit Builder** built-in connector exposes **Create circuit** and separate visual
steps for Hadamard, X, Y, Z, S, adjoint S, T, adjoint T, CNOT, CZ, SWAP, Toffoli, Rx, Ry, and Rz.
Each gate step takes the `circuit` object from the preceding action and returns an updated
`body.circuit`. These steps only append validated instructions: they do not allocate qubits, execute
quantum gates, or measure anything.

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

The connection in `connections.json` uses `/serviceProviders/quantumCircuitBuilder` with
`format=instruction-list-v1`. It requires no external service or credentials. Each append deep-clones
the input descriptor; retrying the same step with the same input produces the same circuit, and
branches cannot mutate each other's gate lists. For a single ordered algorithm, chain gate steps
sequentially; parallel branches are separate circuits and are not automatically merged. Retrying
**execution** can produce different samples because measurement is probabilistic.

All gates run coherently in one Q# invocation per shot, with measurement only at the end. This is
visual circuit construction, not a persistent quantum session, and does not support branching on
intermediate measurements. The deployment script publishes the builder, executor, X demo, visual
workflow, and ten algorithm examples together; no additional Azure resources are required.

With the local host running on port 7071, invoke the visual workflow:

```powershell
$visualUrl = (Invoke-RestMethod -Method Post 'http://localhost:7071/runtime/webhooks/workflow/api/management/workflows/VisualQuantumCircuitWorkflow/triggers/When_a_HTTP_request_is_received/listCallbackUrl?api-version=2020-05-01-preview').value
Invoke-RestMethod -Method Post -Uri $visualUrl -ContentType application/json -Body '{"initialState":"00","shots":200}'
```

In Azure, retrieve this workflow's callback URL from its Request trigger in the designer. Keep the URL
secret because it contains a SAS signature.

## Algorithm demonstration workflows

Each of the following folders under `src\QuantumCircuit.LogicApp` contains its own stateful
`workflow.json`. The gate instructions are separate Circuit Builder actions visible in the designer;
only the final executor creates and measures qubits. POST `{}` or `{"shots":100}` to a workflow's
Request trigger (1-1,000 shots per circuit). These are **small fixed instances**, not general-purpose
implementations accepting arbitrary oracles or problem sizes.

| Workflow | Demonstration and expected result |
|---|---|
| `DeutschJozsaWorkflow` | Two input bits and an ancilla; balanced parity oracle `x0 XOR x1`. Input measurements are `11`; reports `balanced`. |
| `BernsteinVaziraniWorkflow` | Three-bit secret `101` encoded with CNOTs and phase kickback. Reports recovered secret `101`; final ancilla bit varies. |
| `GroverWorkflow` | Two-qubit search, marked item `11`, one oracle/diffusion iteration. Measures `11`. |
| `QuantumFourierTransformWorkflow` | Two-qubit QFT on `01`, with a second independently prepared QFT/inverse-QFT circuit. Forward Z measurements are uniform in expectation; round trip measures `01`. Controlled phases are decomposed into Rz and CNOT gates. |
| `QuantumPhaseEstimationWorkflow` | Two counting qubits estimate the eigenphase of S on `|1>`. Reports phase `0.25` and counting bits `01`; last bit is the eigenstate qubit. |
| `SimonWorkflow` | Two-input oracle `f(x)=x0 XOR x1`, period `11`. Measures input strings `00` or `11`; an observed `11` recovers the period for this two-bit instance. Reports `inconclusive` if finite sampling gives no informative equation. This is not a general GF(2) solver. |
| `SuperdenseCodingWorkflow` | Creates a Bell pair, encodes fixed message `11`, and decodes it. Reports `11`. |
| `GhzWorkflow` | Three-qubit GHZ preparation. Measures only `000` or `111`. |
| `VqeWorkflow` | Single-qubit Hamiltonian Z and ansatz Ry(theta). Sequential classical sweep of theta = 0, pi/2, pi; computes `(count(0)-count(1))/shots` and minimizes it. Includes the exact ground energy `-1`. |
| `QaoaWorkflow` | Depth-one, two-vertex MaxCut. Sequential sweep of three (gamma,beta) pairs; cost is CNOT-Rz(-gamma)-CNOT, mixer is Rx(2 beta) on both qubits. Maximizes measured cut probability and includes parameters achieving `1`. |

VQE and QAOA return `evaluations`, `bestParameters`, `bestObjective`, and `bestCounts`; every iteration
builds a fresh circuit and measures it before classical comparison. Their sweeps are deliberately
small demonstrations rather than full numerical optimizers. Measurement noise can affect which
candidate wins a tie. All examples use computational-basis initialization and final-only
measurement; general teleportation feed-forward, adaptive error correction, and practical Shor
factoring are not included.

`deploy\Test-AlgorithmWorkflows.ps1` supplies algorithm-specific assertions. `deploy.ps1` runs them
after publishing, including QFT reversibility, phase recovery, Simon's finite-shot recovery rule,
and VQE/QAOA objectives recomputed from their measurements. It checks shot totals and allowed
outcomes without requiring an exact random 50/50 split. All workflows use the Q# full-state simulator.

To run the same suite against a local host without provisioning Azure:

```powershell
. .\deploy\Test-AlgorithmWorkflows.ps1
Test-AlgorithmWorkflows -GetCallbackUrl {
    param($name)
    (Invoke-RestMethod -Method Post -Uri "http://localhost:7071/runtime/webhooks/workflow/api/management/workflows/$name/triggers/When_a_HTTP_request_is_received/listCallbackUrl?api-version=2020-05-01-preview").value
}
```

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
4. The script obtains workflow callback URLs and smoke-tests `XGateDemoWorkflow` through `createCircuit -> addX -> runCircuit`, the visual Bell workflow, and all ten algorithm examples. The X check verifies that `X|0>` returns only `1`; probabilistic checks allow sampling variation.

### CI/CD

The same steps map to a pipeline: `az deployment group create` (Bicep) → `dotnet publish` →
`azure/functions-action` or `az logicapp deployment source config-zip`. Keep the callback URL in a
secret store (it contains a SAS signature).

## Notes & limitations

* **QDK version**: the in-process .NET simulators exist only in the *classic* QDK (`Microsoft.Quantum.Sdk`
  0.28, last release). The *Modern QDK* (Q# 1.x) is Rust/Python/JS based and has no .NET API. To target
  real hardware or the Modern QDK, replace `QuantumCircuitExecutor` with a compatible Azure Quantum
  job adapter (for example, compiling and submitting QIR/OpenQASM) while preserving the workflow contract.
* The full-state simulator is limited to 20 qubits for `runCircuit` (state-vector memory grows
  exponentially); the application also caps circuits at 256 gates and 1,000 shots.
* The X gate on a basis state is deterministic, so `counts` is always 100 % on the flipped value. By
  contrast, general `runCircuit` sequences measure only after all gates and can return probabilistic samples.
