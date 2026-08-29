namespace EitHost.Core.Demodulation;

/// <summary>
/// Declares the signed adjacent-drive convention used when exchanging complex data with EIDORS.
/// </summary>
public static class EidorsAdjacentPhasorConvention
{
    public const string HardwareStimulationDefinition =
        "hardware_adjacent_scan:first_electrode=-I,next_electrode=+I;calibrated_against_PyEIDORS_CEM";

    public const string EidorsTargetStimulationDefinition =
        "EIDORS_{ad}: first_electrode=-I,next_electrode=+I";

    public const string MeasurementDefinition = "V(first_electrode)-V(next_electrode)";

    public const string PhasorTimeConvention = "v(t)=Re{V*exp(j*omega*t)}";

    public const string ReferenceMode = "eidors_positive_current_endpoint_voltage_reference";

    public const string ComponentSemantics = "eidors_aligned_signed_boundary_voltage_real_imaginary";

    public const string CurrentReferenceProvenance =
        "phase_derived_from_acquired_positive_current_endpoint_voltage;absolute_current_phase_not_independently_measured";

    public const string HardwareCurrentTerminalMapping =
        "calibrated_first_electrode=-I,next_electrode=+I;PyEIDORS_CEM_median_shape_r=0.994122";

    public const string SignedComplexEidorsReadiness =
        "ready_signed_boundary_voltage;absolute_impedance_phase_requires_independent_current_probe";

    public const string StimulusPairColumnOrder =
        "current_sink_first_electrode,current_source_next_electrode";

    public const string ReferenceEndpointOrder = "V(next_electrode)-V(first_electrode)";
}
