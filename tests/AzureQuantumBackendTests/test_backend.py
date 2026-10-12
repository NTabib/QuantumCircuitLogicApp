import importlib
import inspect
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


class FakeJob:
    id = "fake-job-id"

    def wait_until_completed(self, timeout_secs, print_progress):
        if timeout_secs != 30 or print_progress:
            raise AssertionError("Incorrect job wait settings.")


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


class FakeWorkspace:
    def __init__(self, resource_id, credential):
        if resource_id != "/subscriptions/test/workspaces/demo":
            raise AssertionError("Incorrect workspace resource ID.")


def install_azure_stubs():
    functions = types.ModuleType("azure.functions")

    class FunctionApp:
        def function_name(self, **kwargs):
            return lambda function: function

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
    azure.functions = functions

    quantum = types.ModuleType("azure.quantum")
    quantum.__path__ = []
    quantum.Workspace = FakeWorkspace
    target = types.ModuleType("azure.quantum.target")
    target.__path__ = []
    rigetti = types.ModuleType("azure.quantum.target.rigetti")
    rigetti.Rigetti = FakeRigetti
    rigetti.Result = FakeResult

    sys.modules.update({
        "azure": azure,
        "azure.functions": functions,
        "azure.identity": azure_identity,
        "azure.quantum": quantum,
        "azure.quantum.target": target,
        "azure.quantum.target.rigetti": rigetti,
    })


install_azure_stubs()
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "QuantumCircuit.AzureQuantumBackend"))
backend = importlib.import_module("function_app")


class AzureQuantumBackendTests(unittest.TestCase):
    def test_http_binding_matches_handler_parameter(self):
        trigger_argument = backend.run_quantum_circuit.route_settings["trigger_arg_name"]
        self.assertIn(trigger_argument, inspect.signature(backend.run_quantum_circuit).parameters)

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

    def test_rejects_invalid_or_empty_native_readout(self):
        for readout in ([], [[0]], [[0, 2]], [[0, 0.5]]):
            with self.subTest(readout=readout), self.assertRaises(RuntimeError):
                backend._normalize_readout(readout, 2)

    def test_rejects_non_rigetti_target_before_submission(self):
        payload = {"initialState": "0", "gates": []}
        circuit, shots = backend.build_circuit(payload)
        with patch.dict("os.environ", {
            "AZURE_QUANTUM_WORKSPACE_RESOURCE_ID": "/subscriptions/test/workspaces/demo",
            "AZURE_QUANTUM_TARGET_ID": "ionq.simulator",
        }), self.assertRaisesRegex(RuntimeError, "Rigetti targets only"):
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


if __name__ == "__main__":
    unittest.main()
