namespace QuantumCircuit.QSharp {

	open Microsoft.Quantum.Intrinsic;
	open Microsoft.Quantum.Arrays;
	open Microsoft.Quantum.Measurement;

	/// # Summary
	/// Runs a circuit described by parallel gate/operand arrays and measures only
	/// after every gate has executed, preserving superposition and entanglement
	/// within this Q# invocation.
	///
	/// Gate codes: 0=H, 1=X, 2=Y, 3=Z, 4=S, 5=Adjoint S, 6=T, 7=Adjoint T,
	/// 8=CNOT, 9=CZ, 10=SWAP, 11=Toffoli, 12=Rx, 13=Ry, 14=Rz.
	/// For controlled gates, `controls` (and for Toffoli, `controls2`) are qubit
	/// indices. `targets` is the target or second operand. Angles are in radians.
	operation RunCircuit(
		initialStates : Bool[],
		gateCodes : Int[],
		targets : Int[],
		controls : Int[],
		controls2 : Int[],
		angles : Double[]
	) : Result[] {
		use register = Qubit[Length(initialStates)];

		for i in IndexRange(initialStates) {
			if initialStates[i] {
				X(register[i]);
			}
		}

		for step in IndexRange(gateCodes) {
			let target = targets[step];
			let control = controls[step];
			let control2 = controls2[step];

			if gateCodes[step] == 0 {
				H(register[target]);
			} elif gateCodes[step] == 1 {
				X(register[target]);
			} elif gateCodes[step] == 2 {
				Y(register[target]);
			} elif gateCodes[step] == 3 {
				Z(register[target]);
			} elif gateCodes[step] == 4 {
				S(register[target]);
			} elif gateCodes[step] == 5 {
				Adjoint S(register[target]);
			} elif gateCodes[step] == 6 {
				T(register[target]);
			} elif gateCodes[step] == 7 {
				Adjoint T(register[target]);
			} elif gateCodes[step] == 8 {
				CNOT(register[control], register[target]);
			} elif gateCodes[step] == 9 {
				Controlled Z([register[control]], register[target]);
			} elif gateCodes[step] == 10 {
				SWAP(register[control], register[target]);
			} elif gateCodes[step] == 11 {
				CCNOT(register[control], register[control2], register[target]);
			} elif gateCodes[step] == 12 {
				Rx(angles[step], register[target]);
			} elif gateCodes[step] == 13 {
				Ry(angles[step], register[target]);
			} elif gateCodes[step] == 14 {
				Rz(angles[step], register[target]);
			}
		}

		return ForEach(MResetZ, register);
	}
}
