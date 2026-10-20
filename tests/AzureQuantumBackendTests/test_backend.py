import importlib
import inspect
import json
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch


class FakeHttpResponse:
    def __init__(self, body, status_code, mimetype):
        self.body = body
        self.status_code = status_code
        self.mimetype = mimetype


class FakeAzureError(Exception):
    pass


class FakeJob:
    id = "fake-job-id"

    def wait_until_completed(self, timeout_secs, print_progress):
        if timeout_secs != 30 or print_progress:
            raise AssertionError("Incorrect job wait settings.")

    def get_results(self):
        return {"histogram": {"0": 12, "1": 8}}


class FakeIonQJob(FakeJob):
    def get_results(self):
        return {"histogram": {"0": 12, "1": 8}}


class FakeQuantinuumJob(FakeJob):
    def get_results(self):
        return {"ro": ["00"] * 12 + ["11"] * 8}


class FakeResult:
    def __init__(self, job): pass

    def __getitem__(self, register_name):
        if register_name != "ro":
            raise AssertionError("Incorrect readout register.")
        return [[0, 0]] * 12 + [[1, 1]] * 8


class FakeRigetti:
    def __init__(self, workspace, name):
        if name != "rigetti.sim.qvm":
            raise AssertionError("Incorrect Azure Quantum target.")

    def submit(self, input_data, name, shots):
        if shots != 20:
            raise AssertionError("Incorrect number of submitted shots.")
        if input_data != "DECLARE ro BIT[2]\nH 0\nMEASURE 0 ro[0]\nMEASURE 1 ro[1]\n":
            raise AssertionError("Incorrect submitted Quil program.")
        return FakeJob()


class FakeIonQ:
    def __init__(self, workspace, name):
        if name != "ionq.simulator":
            raise AssertionError("Incorrect Azure Quantum target.")

    def submit(self, circuit, name, shots):
        if shots != 20 or name != "logic-app-quantum-circuit":
            raise AssertionError("Incorrect IonQ job settings.")
        if circuit != {"qubits": 2, "circuit": [{"gate": "h", "target": 0}]}:
            raise AssertionError("Incorrect submitted IonQ circuit.")
        return FakeIonQJob()


class FakeQuantinuum:
    def __init__(self, workspace, name):
        if name != "quantinuum.sim.h2-1sc":
            raise AssertionError("Incorrect Azure Quantum target.")

    def submit(self, circuit, name, shots):
        expected = (
            'OPENQASM 2.0;\ninclude "qelib1.inc";\nqreg q[2];\ncreg ro[2];\n'
            "h q[0];\nmeasure q[0] -> ro[0];\nmeasure q[1] -> ro[1];\n"
        )
        if shots != 20 or name != "logic-app-quantum-circuit" or circuit != expected:
            raise AssertionError("Incorrect submitted Quantinuum OpenQASM program.")
        return FakeQuantinuumJob()


class FakeWorkspace:
    def __init__(self, resource_id, credential):
        if resource_id != "/subscriptions/test/workspaces/demo":
            raise AssertionError("Incorrect workspace resource ID.")


def install_azure_stubs():
    functions = types.ModuleType("azure.functions")

    class FunctionApp:
        def function_name(self, **kwargs):
            def decorate(function):
                function.function_name = kwargs["name"]
                return function
            return decorate

        def route(self, **kwargs):
            def decorate(function):
                function.route_settings = kwargs
                return function
            return decorate

    functions.FunctionApp = FunctionApp
    functions.AuthLevel = types.SimpleNamespace(FUNCTION="function")
    functions.HttpResponse = FakeHttpResponse
    functions.HttpRequest = object
    azure = types.ModuleType("azure")
    azure.__path__ = []
    azure_functions = types.ModuleType("azure.functions")
    azure_functions.__path__ = []
    azure_identity = types.ModuleType("azure.identity")
    azure_identity.DefaultAzureCredential = lambda: object()
    azure_core = types.ModuleType("azure.core")
    azure_core.__path__ = []
    azure_core_exceptions = types.ModuleType("azure.core.exceptions")
    azure_core_exceptions.AzureError = FakeAzureError
    azure.functions = functions
    azure.core = azure_core

    quantum = types.ModuleType("azure.quantum")
    quantum.__path__ = []
    quantum.Workspace = FakeWorkspace
    target = types.ModuleType("azure.quantum.target")
    target.__path__ = []
    rigetti = types.ModuleType("azure.quantum.target.rigetti")
    rigetti.Rigetti = FakeRigetti
    rigetti.Result = FakeResult
    ionq = types.ModuleType("azure.quantum.target.ionq")
    ionq.IonQ = FakeIonQ
    quantinuum = types.ModuleType("azure.quantum.target.quantinuum")
    quantinuum.Quantinuum = FakeQuantinuum

    sys.modules.update({
        "azure": azure,
        "azure.functions": functions,
        "azure.identity": azure_identity,
        "azure.core": azure_core,
        "azure.core.exceptions": azure_core_exceptions,
        "azure.quantum": quantum,
        "azure.quantum.target": target,
        "azure.quantum.target.rigetti": rigetti,
        "azure.quantum.target.ionq": ionq,
        "azure.quantum.target.quantinuum": quantinuum,
    })


install_azure_stubs()
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "QuantumCircuit.AzureQuantumBackend"))
backend = importlib.import_module("function_app")


class AzureQuantumBackendTests(unittest.TestCase):
    def test_http_binding_matches_handler_parameter(self):
        trigger_argument = backend.run_quantum_circuit.route_settings["trigger_arg_name"]
        self.assertIn(trigger_argument, inspect.signature(backend.run_quantum_circuit).parameters)

    def test_logic_app_workflow_owns_scheduling_and_uses_only_run_circuit(self):
        root = Path(__file__).resolve().parents[2] / "src" / "QuantumCircuit.LogicApp"
        schedule = json.loads(
            (root / "HybridProductionSchedulingWorkflow" / "workflow.json").read_text(encoding="utf-8")
        )["definition"]
        actions = schedule["actions"]
        solve_action = actions["Run_QAOA_candidate"]
        self.assertEqual(actions["Greedy_baseline"]["type"], "Compose")
        self.assertEqual(actions["QAOA_cost_scale"]["type"], "Compose")
        self.assertEqual(actions["Select_feasible_measurement"]["type"], "Compose")
        self.assertEqual(actions["Build_successful_result"]["type"], "Compose")
        self.assertEqual(solve_action["type"], "ServiceProvider")
        self.assertEqual(solve_action["retryPolicy"]["type"], "none")
        self.assertEqual(
            solve_action["inputs"]["serviceProviderConfiguration"],
            {
                "connectionName": "azureQuantumCircuit",
                "operationId": "runCircuit",
                "serviceProviderId": "/serviceProviders/azureQuantumCircuit",
            },
        )
        gates = solve_action["inputs"]["parameters"]["gates"]
        self.assertEqual(len(gates), 24)
        self.assertEqual([gate["gate"] for gate in gates].count("H"), 4)
        self.assertEqual([gate["gate"] for gate in gates].count("CNOT"), 8)
        self.assertEqual([gate["gate"] for gate in gates].count("RZ"), 8)
        self.assertEqual([gate["gate"] for gate in gates].count("RX"), 4)
        self.assertIn("1001", actions["Select_feasible_measurement"]["inputs"])
        self.assertIn("0110", actions["Select_feasible_measurement"]["inputs"])
        self.assertNotIn("runHybridSchedule", json.dumps(schedule))

        approval = json.loads(
            (root / "ApplyApprovedProductionScheduleWorkflow" / "workflow.json").read_text(encoding="utf-8")
        )["definition"]
        approval_trigger = approval["triggers"]["When_a_planner_decision_is_received"]
        self.assertIn("decision", approval_trigger["inputs"]["schema"]["required"])
        writeback = approval["actions"]["Decision"]["actions"]["Write_schedule_to_MES"]
        self.assertEqual(writeback["retryPolicy"]["type"], "none")
        self.assertIn("Idempotency-Key", writeback["inputs"]["headers"])

    def test_builds_supported_gate_circuit_and_terminal_measurement(self):
        circuit, shots = backend.build_circuit({
            "initialState": "10",
            "shots": 20,
            "gates": [
                {"gate": "H", "target": 1},
                {"gate": "CNOT", "control": 0, "target": 1},
            ],
        })

        self.assertEqual(shots, 20)
        self.assertEqual(circuit.quil, (
            "DECLARE ro BIT[2]\nX 0\nH 1\nCNOT 0 1\n"
            "MEASURE 0 ro[0]\nMEASURE 1 ro[1]\n"
        ))
        self.assertEqual(circuit.ionq_circuit, {
            "qubits": 2,
            "circuit": [
                {"gate": "x", "target": 0},
                {"gate": "h", "target": 1},
                {"gate": "cnot", "control": 0, "target": 1},
            ],
        })
        self.assertEqual(circuit.openqasm, (
            'OPENQASM 2.0;\ninclude "qelib1.inc";\nqreg q[2];\ncreg ro[2];\n'
            "x q[0];\nh q[1];\ncx q[0],q[1];\n"
            "measure q[0] -> ro[0];\nmeasure q[1] -> ro[1];\n"
        ))

    def test_translates_every_supported_gate_to_quil(self):
        gates = [{"gate": gate, "target": 2} for gate in
                 ("H", "X", "Y", "Z", "S", "S_DAG", "SDG", "T", "T_DAG", "TDG")]
        gates += [{"gate": gate, "control": 0, "target": 2} for gate in
                  ("CNOT", "CX", "CZ", "SWAP")]
        gates += [{"gate": gate, "control": 0, "control2": 1, "target": 2} for gate in
                  ("TOFFOLI", "CCNOT")]
        gates += [{"gate": gate, "angle": -0.5, "target": 2} for gate in ("RX", "RY", "RZ")]
        circuit, _ = backend.build_circuit({"initialState": "000", "gates": gates})
        self.assertEqual(circuit.quil.splitlines()[1:-3], [
            "H 2", "X 2", "Y 2", "Z 2", "S 2", "DAGGER S 2", "DAGGER S 2",
            "T 2", "DAGGER T 2", "DAGGER T 2", "CNOT 0 2", "CNOT 0 2",
            "CZ 0 2", "SWAP 0 2", "CCNOT 0 1 2", "CCNOT 0 1 2",
            "RX(-0.5) 2", "RY(-0.5) 2", "RZ(-0.5) 2",
        ])
        self.assertEqual(circuit.ionq_circuit["circuit"][5:10], [
            {"gate": "si", "target": 2},
            {"gate": "si", "target": 2},
            {"gate": "t", "target": 2},
            {"gate": "ti", "target": 2},
            {"gate": "ti", "target": 2},
        ])
        self.assertEqual(circuit.ionq_circuit["circuit"][10:16], [
            {"gate": "cnot", "control": 0, "target": 2},
            {"gate": "cnot", "control": 0, "target": 2},
            {"gate": "z", "control": 0, "target": 2},
            {"gate": "swap", "targets": [0, 2]},
            {"gate": "not", "controls": [0, 1], "target": 2},
            {"gate": "not", "controls": [0, 1], "target": 2},
        ])
        self.assertEqual(circuit.ionq_circuit["circuit"][16:], [
            {"gate": "rx", "rotation": -0.5, "target": 2},
            {"gate": "ry", "rotation": -0.5, "target": 2},
            {"gate": "rz", "rotation": -0.5, "target": 2},
        ])
        self.assertIn("ccx q[0],q[1],q[2];", circuit.openqasm)
        self.assertIn("cz q[0],q[2];", circuit.openqasm)
        self.assertIn("swap q[0],q[2];", circuit.openqasm)
        self.assertIn("rx(-0.5) q[2];", circuit.openqasm)

    def test_rejects_nonunitary_control_flow(self):
        with self.assertRaisesRegex(ValueError, "unsupported by this Azure Quantum circuit path"):
            backend.build_circuit({
                "initialState": "0",
                "gates": [{"gate": "MEASURE", "target": 0, "measurement": "m"}],
            })

    def test_rejects_invalid_qubit_and_shot_values(self):
        with self.assertRaisesRegex(ValueError, "target.*qubit index"):
            backend.build_circuit({"initialState": "0", "gates": [{"gate": "X", "target": True}]})
        with self.assertRaisesRegex(ValueError, "shots must be an integer"):
            backend.build_circuit({"initialState": "0", "shots": True, "gates": []})

    def test_native_readout_preserves_qubit_zero_first_order(self):
        counts = backend._normalize_readout([[1, 0]] * 3 + [[0, 1]] * 2, 2)
        self.assertEqual(counts, {"10": 3, "01": 2})

    def test_normalizes_ionq_histogram_and_little_endian_outcomes(self):
        counts = backend._normalize_ionq_results({"histogram": {"0": 0.25, "1": 0.75}}, 2, 20)
        self.assertEqual(counts, {"00": 5, "10": 15})

    def test_normalizes_quantinuum_classical_register_order(self):
        counts = backend._normalize_quantinuum_results({"ro": ["10", "01", "10"]}, 2)
        self.assertEqual(counts, {"01": 2, "10": 1})

    def test_rejects_malformed_provider_results(self):
        with self.assertRaisesRegex(RuntimeError, "no IonQ measurement histogram"):
            backend._normalize_ionq_results({}, 2, 20)
        with self.assertRaisesRegex(RuntimeError, "outside the circuit width"):
            backend._normalize_ionq_results({"histogram": {"4": 1}}, 2, 20)
        with self.assertRaisesRegex(RuntimeError, "invalid Quantinuum measurement"):
            backend._normalize_quantinuum_results({"ro": ["0x"]}, 2)

    def test_rejects_invalid_or_empty_native_readout(self):
        for readout in ([], [[0]], [[0, 2]], [[0, 0.5]]):
            with self.subTest(readout=readout), self.assertRaises(RuntimeError):
                backend._normalize_readout(readout, 2)

    def test_rejects_unsupported_provider_before_submission(self):
        payload = {"initialState": "0", "gates": []}
        circuit, shots = backend.build_circuit(payload)
        with patch.dict("os.environ", {
            "AZURE_QUANTUM_WORKSPACE_RESOURCE_ID": "/subscriptions/test/workspaces/demo",
            "AZURE_QUANTUM_TARGET_ID": "pasqal.qpu.fresnel-can1",
        }), self.assertRaisesRegex(RuntimeError, "supports Rigetti, IonQ, and Quantinuum"):
            backend.execute(payload, circuit, shots)

    def test_executes_via_configured_azure_quantum_target(self):
        payload = {
            "initialState": "00",
            "shots": 20,
            "gates": [{"gate": "H", "target": 0}],
        }
        circuit, shots = backend.build_circuit(payload)
        with patch.dict("os.environ", {
            "AZURE_QUANTUM_WORKSPACE_RESOURCE_ID": "/subscriptions/test/workspaces/demo",
            "AZURE_QUANTUM_TARGET_ID": "rigetti.sim.qvm",
            "AZURE_QUANTUM_JOB_TIMEOUT_SECONDS": "30",
        }):
            result = backend.execute(payload, circuit, shots)

        self.assertEqual(result["jobId"], "fake-job-id")
        self.assertEqual(result["targetId"], "rigetti.sim.qvm")
        self.assertEqual(result["counts"], {"00": 12, "11": 8})
        self.assertEqual(result["shots"], 20)
        self.assertAlmostEqual(result["probabilities"]["00"], 0.6)

    def test_executes_via_configured_ionq_target(self):
        payload = {
            "initialState": "00",
            "shots": 20,
            "gates": [{"gate": "H", "target": 0}],
        }
        circuit, shots = backend.build_circuit(payload)
        with patch.dict("os.environ", {
            "AZURE_QUANTUM_WORKSPACE_RESOURCE_ID": "/subscriptions/test/workspaces/demo",
            "AZURE_QUANTUM_TARGET_ID": "ionq.simulator",
            "AZURE_QUANTUM_JOB_TIMEOUT_SECONDS": "30",
        }):
            result = backend.execute(payload, circuit, shots)

        self.assertEqual(result["provider"], "ionq")
        self.assertEqual(result["counts"], {"00": 12, "10": 8})
        self.assertEqual(result["shots"], 20)

    def test_executes_via_configured_quantinuum_target(self):
        payload = {
            "initialState": "00",
            "shots": 20,
            "gates": [{"gate": "H", "target": 0}],
        }
        circuit, shots = backend.build_circuit(payload)
        with patch.dict("os.environ", {
            "AZURE_QUANTUM_WORKSPACE_RESOURCE_ID": "/subscriptions/test/workspaces/demo",
            "AZURE_QUANTUM_TARGET_ID": "quantinuum.sim.h2-1sc",
            "AZURE_QUANTUM_JOB_TIMEOUT_SECONDS": "30",
        }):
            result = backend.execute(payload, circuit, shots)

        self.assertEqual(result["provider"], "quantinuum")
        self.assertEqual(result["counts"], {"00": 12, "11": 8})
        self.assertEqual(result["shots"], 20)


if __name__ == "__main__":
    unittest.main()
