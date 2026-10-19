import json
import logging
import math
import os
from dataclasses import dataclass
from numbers import Integral

import azure.functions as func
from azure.core.exceptions import AzureError
from azure.identity import DefaultAzureCredential
from azure.quantum import Workspace
from azure.quantum.target.ionq import IonQ
from azure.quantum.target.quantinuum import Quantinuum
from azure.quantum.target.rigetti import Rigetti, Result


app = func.FunctionApp()
MAX_QUBITS = 20
MAX_GATES = 256
MAX_SHOTS = 1000
SCHEDULING_QUBITS = 4
MAX_SCHEDULING_COST = 1_000_000_000


@dataclass(frozen=True)
class CircuitProgram:
    quil: str
    ionq_circuit: dict
    openqasm: str
    num_qubits: int


@dataclass(frozen=True)
class ScheduleProblem:
    planning_cycle_id: str
    slots: tuple[str, str]
    jobs: tuple[dict, dict]
    shots: int
    gamma: float
    beta: float


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
    quil_lines = [f"DECLARE ro BIT[{qubit_count}]"]
    qasm_lines = [
        "OPENQASM 2.0;",
        'include "qelib1.inc";',
        f"qreg q[{qubit_count}];",
        f"creg ro[{qubit_count}];",
    ]
    ionq_gates = []
    for qubit, bit in enumerate(initial_state):
        if bit == "1":
            quil_lines.append(f"X {qubit}")
            qasm_lines.append(f"x q[{qubit}];")
            ionq_gates.append({"gate": "x", "target": qubit})

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
            quil_lines.append(f"{operation} {target}")
            qasm_gate = {
                "S_DAG": "sdg", "SDG": "sdg",
                "T_DAG": "tdg", "TDG": "tdg",
            }.get(gate, gate.lower())
            qasm_lines.append(f"{qasm_gate} q[{target}];")
            ionq_gate = {
                "S_DAG": "si", "SDG": "si",
                "T_DAG": "ti", "TDG": "ti",
            }.get(gate, gate.lower())
            ionq_gates.append({"gate": ionq_gate, "target": target})
        elif gate in {"CNOT", "CX", "CZ", "SWAP"}:
            control = _qubit_index(instruction, "control", qubit_count, index)
            if control == target:
                raise ValueError(f"gates[{index}] control and target must differ.")
            operation = "CNOT" if gate == "CX" else gate
            quil_lines.append(f"{operation} {control} {target}")
            if gate in {"CNOT", "CX"}:
                qasm_lines.append(f"cx q[{control}],q[{target}];")
                ionq_gates.append({"gate": "cnot", "control": control, "target": target})
            elif gate == "CZ":
                qasm_lines.append(f"cz q[{control}],q[{target}];")
                ionq_gates.append({"gate": "z", "control": control, "target": target})
            else:
                qasm_lines.append(f"swap q[{control}],q[{target}];")
                ionq_gates.append({"gate": "swap", "targets": [control, target]})
        elif gate in {"TOFFOLI", "CCNOT"}:
            control = _qubit_index(instruction, "control", qubit_count, index)
            control2 = _qubit_index(instruction, "control2", qubit_count, index)
            if len({control, control2, target}) != 3:
                raise ValueError(f"gates[{index}] Toffoli qubits must be distinct.")
            quil_lines.append(f"CCNOT {control} {control2} {target}")
            qasm_lines.append(f"ccx q[{control}],q[{control2}],q[{target}];")
            ionq_gates.append({"gate": "not", "controls": [control, control2], "target": target})
        elif gate in {"RX", "RY", "RZ"}:
            angle = instruction.get("angle")
            if isinstance(angle, bool) or not isinstance(angle, (int, float)) or not math.isfinite(angle):
                raise ValueError(f"gates[{index}] '{gate}' requires a finite numeric angle.")
            numeric_angle = float(angle)
            quil_lines.append(f"{gate}({numeric_angle}) {target}")
            qasm_lines.append(f"{gate.lower()}({numeric_angle}) q[{target}];")
            ionq_gates.append({"gate": gate.lower(), "rotation": numeric_angle, "target": target})
        else:
            raise ValueError(f"gates[{index}] uses unsupported gate '{gate}'.")

    quil_lines.extend(f"MEASURE {qubit} ro[{qubit}]" for qubit in range(qubit_count))
    qasm_lines.extend(f"measure q[{qubit}] -> ro[{qubit}];" for qubit in range(qubit_count))
    return CircuitProgram(
        "\n".join(quil_lines) + "\n",
        {"qubits": qubit_count, "circuit": ionq_gates},
        "\n".join(qasm_lines) + "\n",
        qubit_count,
    ), shots


def build_schedule_problem(payload: dict) -> ScheduleProblem:
    if not isinstance(payload, dict):
        raise ValueError("The request body must be a JSON object.")

    cycle_id = payload.get("planningCycleId")
    if not isinstance(cycle_id, str) or not cycle_id.strip() or len(cycle_id) > 128:
        raise ValueError("planningCycleId must be a non-empty string of at most 128 characters.")
    cycle_id = cycle_id.strip()

    slots = payload.get("slots")
    if (
        not isinstance(slots, list)
        or len(slots) != 2
        or any(not isinstance(slot, str) or not slot.strip() for slot in slots)
        or len(set(slots)) != 2
    ):
        raise ValueError("slots must contain exactly two distinct, non-empty slot IDs.")
    slots = [slot.strip() for slot in slots]
    if len(set(slots)) != 2:
        raise ValueError("slots must contain exactly two distinct, non-empty slot IDs.")

    raw_jobs = payload.get("jobs")
    if not isinstance(raw_jobs, list) or len(raw_jobs) != 2:
        raise ValueError("jobs must contain exactly two jobs for this QAOA pilot.")

    jobs = []
    job_ids = set()
    for index, job in enumerate(raw_jobs):
        if not isinstance(job, dict):
            raise ValueError(f"jobs[{index}] must be an object.")
        job_id = job.get("id")
        costs = job.get("slotCosts")
        if not isinstance(job_id, str) or not job_id.strip():
            raise ValueError(f"jobs[{index}].id must be a unique, non-empty string.")
        job_id = job_id.strip()
        if job_id in job_ids:
            raise ValueError(f"jobs[{index}].id must be a unique, non-empty string.")
        if not isinstance(costs, list) or len(costs) != 2:
            raise ValueError(f"jobs[{index}].slotCosts must provide one cost per slot.")
        if any(
            isinstance(cost, bool)
            or not isinstance(cost, (int, float))
            or cost < 0
            or cost > MAX_SCHEDULING_COST
            or not math.isfinite(cost)
            for cost in costs
        ):
            raise ValueError(f"jobs[{index}].slotCosts must contain finite, non-negative numbers.")
        job_ids.add(job_id)
        jobs.append({"id": job_id, "slotCosts": [float(cost) for cost in costs]})

    shots = payload.get("shots", 100)
    if isinstance(shots, bool) or not isinstance(shots, int) or not 1 <= shots <= MAX_SHOTS:
        raise ValueError(f"shots must be an integer between 1 and {MAX_SHOTS}.")

    qaoa = payload.get("qaoa", {})
    if not isinstance(qaoa, dict):
        raise ValueError("qaoa must be an object containing optional gamma and beta angles.")
    gamma = qaoa.get("gamma", 0.7)
    beta = qaoa.get("beta", 0.4)
    if any(
        isinstance(angle, bool)
        or not isinstance(angle, (int, float))
        or not -math.pi <= angle <= math.pi
        or not math.isfinite(angle)
        for angle in (gamma, beta)
    ):
        raise ValueError("qaoa.gamma and qaoa.beta must be finite numbers between -pi and pi.")

    return ScheduleProblem(
        cycle_id,
        (slots[0], slots[1]),
        (jobs[0], jobs[1]),
        shots,
        float(gamma),
        float(beta),
    )


def build_classical_schedule(problem: ScheduleProblem) -> dict:
    available_slots = set(range(len(problem.slots)))
    assignments = []
    for job in problem.jobs:
        slot_index = min(available_slots, key=lambda slot: (job["slotCosts"][slot], slot))
        available_slots.remove(slot_index)
        assignments.append({
            "jobId": job["id"],
            "slotId": problem.slots[slot_index],
            "cost": job["slotCosts"][slot_index],
        })
    return {
        "method": "classical-greedy",
        "assignments": assignments,
        "totalCost": sum(assignment["cost"] for assignment in assignments),
        "feasible": True,
    }


def build_qaoa_schedule_circuit(problem: ScheduleProblem) -> dict:
    cost_scale = sum(max(job["slotCosts"]) for job in problem.jobs) or 1.0
    penalty = 2.0
    linear = [0.0] * SCHEDULING_QUBITS
    quadratic: dict[tuple[int, int], float] = {}

    for job_index, job in enumerate(problem.jobs):
        for slot_index, cost in enumerate(job["slotCosts"]):
            variable = job_index * 2 + slot_index
            linear[variable] += cost / cost_scale - 2.0 * penalty

        first = job_index * 2
        second = first + 1
        quadratic[(first, second)] = quadratic.get((first, second), 0.0) + 2.0 * penalty

    for slot_index in range(2):
        first = slot_index
        second = 2 + slot_index
        quadratic[(first, second)] = quadratic.get((first, second), 0.0) + 2.0 * penalty

    z_terms = []
    for variable, coefficient in enumerate(linear):
        pair_contribution = sum(
            value / 4.0
            for (first, second), value in quadratic.items()
            if variable in (first, second)
        )
        z_terms.append(-coefficient / 2.0 - pair_contribution)

    gates = [{"gate": "H", "target": qubit} for qubit in range(SCHEDULING_QUBITS)]
    for variable, coefficient in enumerate(z_terms):
        if coefficient:
            gates.append({"gate": "RZ", "target": variable, "angle": 2.0 * problem.gamma * coefficient})

    for (control, target), coefficient in sorted(quadratic.items()):
        coupling = coefficient / 4.0
        gates.extend([
            {"gate": "CNOT", "control": control, "target": target},
            {"gate": "RZ", "target": target, "angle": 2.0 * problem.gamma * coupling},
            {"gate": "CNOT", "control": control, "target": target},
        ])

    gates.extend(
        {"gate": "RX", "target": qubit, "angle": 2.0 * problem.beta}
        for qubit in range(SCHEDULING_QUBITS)
    )
    return {"initialState": "0000", "gates": gates, "shots": problem.shots}


def decode_schedule_measurement(state: str, problem: ScheduleProblem) -> dict | None:
    if len(state) != SCHEDULING_QUBITS or any(bit not in "01" for bit in state):
        return None

    selected_slots = []
    for job_index, job in enumerate(problem.jobs):
        job_slots = [
            slot_index
            for slot_index in range(2)
            if state[job_index * 2 + slot_index] == "1"
        ]
        if len(job_slots) != 1:
            return None
        selected_slots.append(job_slots[0])
    if len(set(selected_slots)) != 2:
        return None

    assignments = [
        {
            "jobId": job["id"],
            "slotId": problem.slots[slot_index],
            "cost": job["slotCosts"][slot_index],
        }
        for job, slot_index in zip(problem.jobs, selected_slots)
    ]
    return {
        "method": "QAOA",
        "assignments": assignments,
        "totalCost": sum(assignment["cost"] for assignment in assignments),
        "feasible": True,
        "measurement": state,
    }


def choose_quantum_schedule(counts: dict, problem: ScheduleProblem) -> dict | None:
    candidates = {}
    for state, count in counts.items():
        candidate = decode_schedule_measurement(state, problem)
        if candidate is not None:
            candidates[state] = (candidate, count)
    if not candidates:
        return None

    state, (candidate, count) = min(
        candidates.items(),
        key=lambda item: (item[1][0]["totalCost"], -item[1][1], item[0]),
    )
    selected = dict(candidate)
    selected["sampleCount"] = count
    return selected


def run_hybrid_schedule(problem: ScheduleProblem) -> dict:
    baseline = build_classical_schedule(problem)
    quantum_status = "NotConfigured"
    quantum_error = None
    quantum_candidate = None
    quantum_job = None

    if os.environ.get("AZURE_QUANTUM_WORKSPACE_RESOURCE_ID") and os.environ.get("AZURE_QUANTUM_TARGET_ID"):
        circuit_payload = build_qaoa_schedule_circuit(problem)
        circuit, shots = build_circuit(circuit_payload)
        try:
            quantum_job = execute(circuit_payload, circuit, shots)
            quantum_status = "Succeeded"
            quantum_candidate = choose_quantum_schedule(quantum_job["counts"], problem)
            if quantum_candidate is None:
                quantum_status = "NoFeasibleSamples"
        except (AzureError, TimeoutError, RuntimeError) as error:
            quantum_status = "Failed"
            quantum_error = str(error)[:500]
            logging.warning(
                "Quantum scheduling candidate failed for cycle %s: %s",
                problem.planning_cycle_id,
                quantum_error,
            )
    else:
        quantum_error = (
            "Configure AZURE_QUANTUM_WORKSPACE_RESOURCE_ID and AZURE_QUANTUM_TARGET_ID "
            "to run the QAOA candidate."
        )

    selected = baseline
    decision = "ClassicalFallback"
    if quantum_candidate is not None and quantum_candidate["totalCost"] < baseline["totalCost"]:
        selected = quantum_candidate
        decision = "QuantumImproved"
    elif quantum_status == "Succeeded":
        decision = "ClassicalBaselineRetained"

    absolute_improvement = baseline["totalCost"] - selected["totalCost"]
    relative_improvement = absolute_improvement / baseline["totalCost"] if baseline["totalCost"] else 0.0
    result = {
        "backend": "HybridProductionScheduling",
        "planningCycleId": problem.planning_cycle_id,
        "decision": decision,
        "baseline": baseline,
        "quantumCandidate": {
            "status": quantum_status,
            "targetId": quantum_job["targetId"] if quantum_job else None,
            "jobId": quantum_job["jobId"] if quantum_job else None,
            "shots": quantum_job["shots"] if quantum_job else 0,
            "schedule": quantum_candidate,
            "error": quantum_error,
        },
        "selectedSchedule": selected,
        "improvement": {
            "absoluteCostReduction": absolute_improvement,
            "relativeCostReduction": relative_improvement,
        },
        "approval": {"required": True, "status": "Pending"},
    }
    logging.info(
        "Scheduling cycle %s completed with decision %s",
        problem.planning_cycle_id,
        decision,
    )
    return result


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


def _normalize_ionq_results(results: dict, qubit_count: int, requested_shots: int) -> dict[str, int]:
    histogram = results.get("histogram") if isinstance(results, dict) else None
    if not isinstance(histogram, dict) or not histogram:
        raise RuntimeError("Azure Quantum returned no IonQ measurement histogram.")

    weighted_counts: dict[str, float] = {}
    for outcome, value in histogram.items():
        try:
            outcome_index = int(outcome)
        except (TypeError, ValueError) as error:
            raise RuntimeError("Azure Quantum returned an invalid IonQ histogram outcome.") from error
        if not 0 <= outcome_index < 2 ** qubit_count:
            raise RuntimeError("Azure Quantum returned an IonQ outcome outside the circuit width.")
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
            raise RuntimeError("Azure Quantum returned an invalid IonQ histogram weight.")
        state = format(outcome_index, f"0{qubit_count}b")[::-1]
        weighted_counts[state] = weighted_counts.get(state, 0.0) + float(value)

    total_weight = sum(weighted_counts.values())
    if total_weight <= 0:
        raise RuntimeError("Azure Quantum returned an empty IonQ measurement histogram.")
    if total_weight <= 1.0:
        weighted_counts = {state: weight * requested_shots for state, weight in weighted_counts.items()}
        total_weight = float(requested_shots)

    target_shots = int(math.floor(total_weight + 0.5))
    counts = {state: int(math.floor(weight)) for state, weight in weighted_counts.items()}
    remaining = target_shots - sum(counts.values())
    remainder_order = sorted(
        weighted_counts,
        key=lambda state: (weighted_counts[state] - counts[state], state),
        reverse=True,
    )
    for state in remainder_order[:remaining]:
        counts[state] += 1
    counts = {state: count for state, count in counts.items() if count > 0}
    if not counts:
        raise RuntimeError("Azure Quantum returned no IonQ measurement counts.")
    return counts


def _normalize_quantinuum_results(results: dict, qubit_count: int) -> dict[str, int]:
    readouts = results.get("ro") if isinstance(results, dict) else None
    if isinstance(readouts, str):
        readouts = [readouts]
    if not isinstance(readouts, list) or not readouts:
        raise RuntimeError("Azure Quantum returned no Quantinuum readout register.")

    counts: dict[str, int] = {}
    for readout in readouts:
        if not isinstance(readout, str) or len(readout) != qubit_count or any(bit not in "01" for bit in readout):
            raise RuntimeError("Azure Quantum returned an invalid Quantinuum measurement bit string.")
        state = readout[::-1]
        counts[state] = counts.get(state, 0) + 1
    return counts


def execute(payload: dict, circuit: CircuitProgram, requested_shots: int) -> dict:
    workspace_resource_id = os.environ.get("AZURE_QUANTUM_WORKSPACE_RESOURCE_ID")
    target_id = os.environ.get("AZURE_QUANTUM_TARGET_ID")
    if not workspace_resource_id or not target_id:
        raise RuntimeError(
            "Configure AZURE_QUANTUM_WORKSPACE_RESOURCE_ID and AZURE_QUANTUM_TARGET_ID "
            "on the Azure Quantum backend Function App."
        )

    provider = target_id.split(".", 1)[0].lower()
    target_types = {
        "rigetti": Rigetti,
        "ionq": IonQ,
        "quantinuum": Quantinuum,
    }
    if provider not in target_types:
        raise RuntimeError(
            f"Unsupported Azure Quantum provider '{provider}'. This backend supports Rigetti, IonQ, and "
            "Quantinuum gate-circuit targets; pulse-based providers such as Pasqal are not supported."
        )

    timeout = int(os.environ.get("AZURE_QUANTUM_JOB_TIMEOUT_SECONDS", "180"))
    if not 1 <= timeout <= 180:
        raise RuntimeError("AZURE_QUANTUM_JOB_TIMEOUT_SECONDS must be between 1 and 180.")

    workspace = Workspace(
        resource_id=workspace_resource_id,
        credential=DefaultAzureCredential(),
    )
    target = target_types[provider](workspace=workspace, name=target_id)
    if provider == "rigetti":
        job = target.submit(
            input_data=circuit.quil,
            name="logic-app-quantum-circuit",
            shots=requested_shots,
        )
    elif provider == "ionq":
        job = target.submit(
            circuit=circuit.ionq_circuit,
            name="logic-app-quantum-circuit",
            shots=requested_shots,
        )
    else:
        job = target.submit(
            circuit=circuit.openqasm,
            name="logic-app-quantum-circuit",
            shots=requested_shots,
        )
    job_id = job.id
    logging.info("Azure Quantum job submitted: %s", job_id)
    job.wait_until_completed(timeout_secs=timeout, print_progress=False)
    if provider == "rigetti":
        counts = _normalize_readout(Result(job)["ro"], circuit.num_qubits)
    elif provider == "ionq":
        counts = _normalize_ionq_results(job.get_results(), circuit.num_qubits, requested_shots)
    else:
        counts = _normalize_quantinuum_results(job.get_results(), circuit.num_qubits)
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


@app.function_name(name="run_hybrid_schedule")
@app.route(
    route="run-hybrid-schedule",
    methods=["POST"],
    auth_level=func.AuthLevel.FUNCTION,
    trigger_arg_name="request",
)
def run_hybrid_schedule_http(request: func.HttpRequest) -> func.HttpResponse:
    try:
        problem = build_schedule_problem(request.get_json())
    except (ValueError, TypeError, json.JSONDecodeError) as error:
        return func.HttpResponse(
            json.dumps({"error": str(error)}),
            status_code=400,
            mimetype="application/json",
        )

    result = run_hybrid_schedule(problem)
    return func.HttpResponse(json.dumps(result), status_code=200, mimetype="application/json")
