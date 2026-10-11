import importlib
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch


class FakeCircuit:
    def __init__(self, qubits, classical_bits):
        self.num_qubits = qubits
        self.classical_bits = classical_bits
        self.operations = []

    def _add(self, name, *args):
        self.operations.append((name, *args))

    def h(self, *args): self._add("h", *args)
    def x(self, *args): self._add("x", *args)
    def y(self, *args): self._add("y", *args)
    def z(self, *args): self._add("z", *args)
    def s(self, *args): self._add("s", *args)
    def sdg(self, *args): self._add("sdg", *args)
    def t(self, *args): self._add("t", *args)
    def tdg(self, *args): self._add("tdg", *args)
    def cx(self, *args): self._add("cx", *args)
    def cz(self, *args): self._add("cz", *args)
    def swap(self, *args): self._add("swap", *args)
    def ccx(self, *args): self._add("ccx", *args)
    def rx(self, *args): self._add("rx", *args)
    def ry(self, *args): self._add("ry", *args)
    def rz(self, *args): self._add("rz", *args)
    def measure(self, *args): self._add("measure", *args)


class FakeHttpResponse:
    def __init__(self, body, status_code, mimetype):
        self.body = body
        self.status_code = status_code
        self.mimetype = mimetype


class FakeJob:
    def job_id(self): return "fake-job-id"
    def result(self, timeout): return FakeResult()


class FakeResult:
    def get_counts(self, circuit): return {"00": 12, "11": 8}


class FakeBackend:
    def run(self, circuit, shots):
        if shots != 20:
            raise AssertionError("Incorrect number of submitted shots.")
        return FakeJob()


class FakeWorkspace:
    def __init__(self, resource_id, credential):
        if resource_id != "/subscriptions/test/workspaces/demo":
            raise AssertionError("Incorrect workspace resource ID.")


class FakeProvider:
    def __init__(self, workspace): pass
    def get_backend(self, target_id):
        if target_id != "rigetti.sim.qvm":
            raise AssertionError("Incorrect Azure Quantum target.")
        return FakeBackend()


def install_qdk_stubs():
    functions = types.ModuleType("azure.functions")

    class FunctionApp:
        def route(self, **kwargs):
            return lambda function: function

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

    qdk = types.ModuleType("qdk")
    qdk.__path__ = []
    qdk_azure = types.ModuleType("qdk.azure")
    qdk_azure.__path__ = []
    qdk_azure.Workspace = FakeWorkspace
    qdk_qiskit = types.ModuleType("qdk.azure.qiskit")
    qdk_qiskit.AzureQuantumProvider = FakeProvider
    qiskit = types.ModuleType("qiskit")
    qiskit.QuantumCircuit = FakeCircuit

    sys.modules.update({
        "azure": azure,
        "azure.functions": functions,
        "azure.identity": azure_identity,
        "qdk": qdk,
        "qdk.azure": qdk_azure,
        "qdk.azure.qiskit": qdk_qiskit,
        "qiskit": qiskit,
    })


install_qdk_stubs()
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src" / "QuantumCircuit.AzureQuantumBackend"))
backend = importlib.import_module("function_app")


class AzureQuantumBackendTests(unittest.TestCase):
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
        self.assertEqual(circuit.operations[0], ("x", 0))
        self.assertEqual(circuit.operations[1], ("h", 1))
        self.assertEqual(circuit.operations[2], ("cx", 0, 1))
        self.assertEqual(circuit.operations[3][0], "measure")

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

    def test_normalizes_qiskit_little_endian_counts_to_qubit_zero_first(self):
        counts = backend._normalize_counts({"01": 3, "10": 2}, 2)
        self.assertEqual(counts, {"10": 3, "01": 2})

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
