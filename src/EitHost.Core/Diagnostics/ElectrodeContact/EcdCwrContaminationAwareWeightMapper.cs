namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed class EcdCwrContaminationAwareWeightMapper
{
    public double[] Map(
        IReadOnlyList<double>? electrodeScores,
        IReadOnlyList<ElectrodeEvidenceKind>? evidenceKinds,
        IReadOnlyList<ElectrodeFaultType>? faultTypes,
        EcdCwrContinuousWeightMapperOptions? options = null)
    {
        options ??= new EcdCwrContinuousWeightMapperOptions();
        var effectiveScores = BuildEffectiveScores(electrodeScores, evidenceKinds, faultTypes);
        return new EcdCwrContinuousWeightMapper().Map(effectiveScores, options);
    }

    public double[] BuildEffectiveScores(
        IReadOnlyList<double>? electrodeScores,
        IReadOnlyList<ElectrodeEvidenceKind>? evidenceKinds,
        IReadOnlyList<ElectrodeFaultType>? faultTypes)
    {
        var effective = new double[16];
        if (electrodeScores is not { Count: 16 } ||
            evidenceKinds is not { Count: 16 } ||
            faultTypes is not { Count: 16 })
        {
            return effective;
        }

        for (var electrode = 0; electrode < effective.Length; electrode++)
        {
            var kind = evidenceKinds[electrode];
            var faultType = faultTypes[electrode];
            var contaminationSupported =
                (kind & (ElectrodeEvidenceKind.EvidenceB | ElectrodeEvidenceKind.Saturation)) != 0 ||
                faultType is ElectrodeFaultType.DrivePairLink or ElectrodeFaultType.AcquisitionChannel;
            if (!contaminationSupported)
            {
                continue;
            }

            var score = electrodeScores[electrode];
            effective[electrode] = double.IsFinite(score) ? Math.Max(0.0, score) : 0.0;
        }

        return effective;
    }

    public static string CreatePolicyVersion(EcdCwrContinuousWeightMapperOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return $"ecd-cwr-p2-hill-v2-contamination:q0={options.Q0:G4}:p={options.Power:G4}:min={options.MinimumWeight:G4}";
    }
}
