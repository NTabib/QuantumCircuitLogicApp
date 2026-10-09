namespace QuantumCircuit.QSharp {

	open Microsoft.Quantum.Intrinsic;
	open Microsoft.Quantum.Arrays;
	open Microsoft.Quantum.Measurement;

	/// # Summary
	/// Applies a parameterized Y-axis rotation to one qubit.
	operation ApplyRYFromParameters(angle : Double, target : Qubit) : Unit {
		Ry(angle, target);
	}

	/// # Summary
	/// Executes ordered gates, mid-circuit measurements, resets, and one-level
	/// measurement-conditioned gate blocks before returning named and final results.
	///
	/// Gate codes: 0-14 are the primitive gates, 15=measure, and 16=reset.
	/// For controlled gates, `controls` and `controls2` contain qubit indices;
	/// angles are in radians. A condition slot of -1 means an unconditional instruction.
	operation RunCircuit(
		initialStates : Bool[],
		gateCodes : Int[],
		targets : Int[],
		controls : Int[],
		controls2 : Int[],
		angles : Double[],
		measurementSlots : Int[],
		conditionSlots : Int[],
		conditionValues : Bool[],
		measurementCount : Int
	) : Result[] {
		use register = Qubit[Length(initialStates)];
		mutable outputResults = [Zero];

		for _ in 1 .. (measurementCount + Length(initialStates) - 1) {
			set outputResults += [Zero];
		}

		for i in IndexRange(initialStates) {
			if initialStates[i] {
				X(register[i]);
			}
		}

		for step in IndexRange(gateCodes) {
			let target = targets[step];
			let code = gateCodes[step];

			if code == 15 {
				set outputResults w/= measurementSlots[step] <- M(register[target]);
			} elif code == 16 {
				Reset(register[target]);
			} else {
				let conditionSlot = conditionSlots[step];
				if conditionSlot < 0 {
					ApplyGate(code, target, controls[step], controls2[step], angles[step], register);
				} elif ((outputResults[conditionSlot] == One) == conditionValues[step]) {
					ApplyGate(code, target, controls[step], controls2[step], angles[step], register);
				}
			}
		}

		for i in IndexRange(register) {
			set outputResults w/= measurementCount + i <- MResetZ(register[i]);
		}

		return outputResults;
	}

	operation ApplyGate(code : Int, target : Int, control : Int, control2 : Int, angle : Double, register : Qubit[]) : Unit {
		if code == 0 {
			H(register[target]);
		} elif code == 1 {
			X(register[target]);
		} elif code == 2 {
			Y(register[target]);
		} elif code == 3 {
			Z(register[target]);
		} elif code == 4 {
			S(register[target]);
		} elif code == 5 {
			Adjoint S(register[target]);
		} elif code == 6 {
			T(register[target]);
		} elif code == 7 {
			Adjoint T(register[target]);
		} elif code == 8 {
			CNOT(register[control], register[target]);
		} elif code == 9 {
			Controlled Z([register[control]], register[target]);
		} elif code == 10 {
			SWAP(register[control], register[target]);
		} elif code == 11 {
			CCNOT(register[control], register[control2], register[target]);
		} elif code == 12 {
			Rx(angle, register[target]);
		} elif code == 13 {
			ApplyRYFromParameters(angle, register[target]);
		} elif code == 14 {
			Rz(angle, register[target]);
		}
	}

	operation ReferenceMeasurementResetControl() : Result[] {
		use register = Qubit[2];
		let measuredZero = M(register[0]);
		X(register[0]);
		let measuredOne = M(register[0]);
		Reset(register[0]);

		if measuredOne == One {
			X(register[1]);
		} else {
			X(register[0]);
		}

		if measuredZero == One {
			X(register[0]);
		} else {
			X(register[1]);
		}

		mutable results = [measuredZero, measuredOne];
		for result in ForEach(MResetZ, register) {
			set results += [result];
		}
		return results;
	}

	operation ReferenceMeasurementCollapseAndReset() : Result[] {
		use register = Qubit[1];
		H(register[0]);
		let first = M(register[0]);
		let repeated = M(register[0]);
		Reset(register[0]);
		let terminal = MResetZ(register[0]);
		return [first, repeated, terminal];
	}
}
