import json
import logging
import math
import os
from dataclasses import dataclass
from numbers import Integral

import azure.functions as func
from azure.identity import DefaultAzureCredential
from azure.quantum import Workspace
from azure.quantum.target.rigetti import Rigetti, Result


app = func.FunctionApp()
MAX_QUBITS = 20
MAX_GATES = 256
MAX_SHOTS = 1000


@dataclass(frozen=True)
class CircuitProgram:
    quil: str
    num_qubits: int


def build_circuit(payload: dict) -> tuple[CircuitProgram, int]:
    initial_state = payload.get("initialState")
    gates = payload.get("gates")
    shots = payload.get("shots", 100)

    if not isinstance(initial_state, str) or not initial_state or len(initial_state) > MAX_QUBITS:
        raise ValueError(f"initialState must contain 1-{MAX_QUBITS} bits.")
    if any(bit not in "01" for bit in initial_state):
        raise ValueError("initialState must be a computational-basis bit string.")
    if not isinstance(gates, list) or len(gates) > MAX_GATES:
        raise ValueError(f"gates must be an array containing at most {MAX_GATES} gates.")
    if isinstance(shots, bool) or not isinstance(shots, int) or not 1 <= shots <= MAX_SHOTS:
        raise ValueError(f"shots must be an integer between 1 and {MAX_SHOTS}.")

    qubit_count = len(initial_state)
    lines = [f"DECLARE ro BIT[{qubit_count}]"]
    for qubit, bit in enumerate(initial_state):
        if bit == "1":
            lines.append(f"X {qubit}")

    for index, instruction in enumerate(gates):
        if not isinstance(instruction, dict) or not isinstance(instruction.get("gate"), str):
            raise ValueError(f"gates[{index}] must be an object with a gate name.")
        gate = instruction["gate"].strip().upper()
        if gate in {"MEASURE", "RESET", "IF", "CALL"}:
            raise ValueError(
                f"gates[{index}] '{gate}' is unsupported by this Azure Quantum circuit path; "
                "submit only unitary gates with terminal measurement."
            )

        target = _qubit_index(instruction, "target", qubit_count, index)
        if gate in {"H", "X", "Y", "Z", "S", "S_DAG", "SDG", "T", "T_DAG", "TDG"}:
            operation = {"S_DAG": "DAGGER S", "SDG": "DAGGER S",
                         "T_DAG": "DAGGER T", "TDG": "DAGGER T"}.get(gate, gate)
            lines.append(f"{operation} {target}")
        elif gate in {"CNOT", "CX", "CZ", "SWAP"}:
            control = _qubit_index(instruction, "control", qubit_count, index)
            if control == target:
                raise ValueError(f"gates[{index}] control and target must differ.")
            operation = "CNOT" if gate == "CX" else gate
            lines.append(f"{operation} {control} {target}")
        elif gate in {"TOFFOLI", "CCNOT"}:
            control = _qubit_index(instruction, "control", qubit_count, index)
            control2 = _qubit_index(instruction, "control2", qubit_count, index)
            if len({control, control2, target}) != 3:
                raise ValueError(f"gates[{index}] Toffoli qubits must be distinct.")
            lines.append(f"CCNOT {control} {control2} {target}")
        elif gate in {"RX", "RY", "RZ"}:
            angle = instruction.get("angle")
            if isinstance(angle, bool) or not isinstance(angle, (int, float)) or not math.isfinite(angle):
                raise ValueError(f"gates[{index}] '{gate}' requires a finite numeric angle.")
            lines.append(f"{gate}({float(angle)}) {target}")
        else:
            raise ValueError(f"gates[{index}] uses unsupported gate '{gate}'.")

    lines.extend(f"MEASURE {qubit} ro[{qubit}]" for qubit in range(qubit_count))
    return CircuitProgram("\n".join(lines) + "\n", qubit_count), shots


def _qubit_index(instruction: dict, field: str, qubit_count: int, gate_index: int) -> int:
    value = instruction.get(field)
    if isinstance(value, bool) or not isinstance(value, int) or not 0 <= value < qubit_count:
        raise ValueError(f"gates[{gate_index}] '{field}' must be a qubit index in [0, {qubit_count - 1}].")
    return value


def _normalize_readout(readout: list, qubit_count: int) -> dict[str, int]:
    counts: dict[str, int] = {}
    for shot in readout:
        if len(shot) != qubit_count:
            raise RuntimeError("Azure Quantum returned a measurement with an unexpected bit width.")
        if any(not isinstance(bit, Integral) or bit not in (0, 1) for bit in shot):
            raise RuntimeError("Azure Quantum returned an invalid measurement bit.")
        # ro[i] stores qubit i, so native Rigetti readout is already qubit-0-first.
        state = "".join(str(int(bit)) for bit in shot)
        counts[state] = counts.get(state, 0) + 1
    if not counts:
        raise RuntimeError("Azure Quantum returned no measurement counts.")
    return counts


def execute(payload: dict, circuit: CircuitProgram, requested_shots: int) -> dict:
    workspace_resource_id = os.environ.get("AZURE_QUANTUM_WORKSPACE_RESOURCE_ID")
    target_id = os.environ.get("AZURE_QUANTUM_TARGET_ID")
    if not workspace_resource_id or not target_id:
        raise RuntimeError(
            "Configure AZURE_QUANTUM_WORKSPACE_RESOURCE_ID and AZURE_QUANTUM_TARGET_ID "
            "on the Azure Quantum backend Function App."
        )

    if target_id != "rigetti.sim.qvm" and not target_id.startswith("rigetti.qpu."):
        raise RuntimeError("This backend supports Rigetti targets only; configure rigetti.sim.qvm or a Rigetti QPU.")

    timeout = int(os.environ.get("AZURE_QUANTUM_JOB_TIMEOUT_SECONDS", "180"))
    if not 1 <= timeout <= 180:
        raise RuntimeError("AZURE_QUANTUM_JOB_TIMEOUT_SECONDS must be between 1 and 180.")

    workspace = Workspace(
        resource_id=workspace_resource_id,
        credential=DefaultAzureCredential(),
    )
    target = Rigetti(workspace=workspace, name=target_id)
    job = target.submit(
        input_data=circuit.quil,
        name="logic-app-quantum-circuit",
        shots=requested_shots,
    )
    job_id = job.id
    logging.info("Azure Quantum job submitted: %s", job_id)
    job.wait_until_completed(timeout_secs=timeout, print_progress=False)
    counts = _normalize_readout(Result(job)["ro"], circuit.num_qubits)
    completed_shots = sum(counts.values())

    return {
        "backend": "AzureQuantum",
        "provider": target_id.split(".", 1)[0],
        "targetId": target_id,
        "jobId": job_id,
        "status": "Succeeded",
        "qubitCount": circuit.num_qubits,
        "gateCount": len(payload["gates"]),
        "shots": completed_shots,
        "requestedShots": requested_shots,
        "sample": max(counts, key=counts.get),
        "counts": counts,
        "probabilities": {state: count / completed_shots for state, count in counts.items()},
    }


@app.function_name(name="run_quantum_circuit")
@app.route(
    route="run-quantum-circuit",
    methods=["POST"],
    auth_level=func.AuthLevel.FUNCTION,
    trigger_arg_name="request",
)
def run_quantum_circuit(request: func.HttpRequest) -> func.HttpResponse:
    try:
        payload = request.get_json()
        if not isinstance(payload, dict):
            raise ValueError("The request body must be a JSON object.")
        circuit, requested_shots = build_circuit(payload)
    except (ValueError, TypeError, json.JSONDecodeError) as error:
        return func.HttpResponse(
            json.dumps({"error": str(error)}),
            status_code=400,
            mimetype="application/json",
        )

    result = execute(payload, circuit, requested_shots)
    logging.info("Azure Quantum job completed: %s", result["jobId"])
    return func.HttpResponse(json.dumps(result), status_code=200, mimetype="application/json")
