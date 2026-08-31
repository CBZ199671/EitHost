using EitHost.Core.Diagnostics.ElectrodeContact;

namespace EitHost.Core.Application.Realtime;

public static class RealtimeReferenceTolerancePolicy
{
    public const string ProfileVersion = "plant-cutting-balanced-v1";

    public static EcdCwrReferenceStationarityOptions CreateStationarityOptions()
    {
        return new EcdCwrReferenceStationarityOptions(
            MinimumDurationSeconds: 55.0,
            RequiredStableUpdates: 3,
            AdaptiveConfidenceZ: 3.0,
            MaximumAdaptiveShapeResidualPerMinute: 1.0e-3,
            CoherentStepSigmaThreshold: 8.0,
            AllowCommonScaleDrift: true);
    }

    public static ElectrodeContactMonitorOptions CreateContactMonitorOptions()
    {
        return new ElectrodeContactMonitorOptions
        {
            ReferenceInvalidationCriticalConfirmationFrames = 3,
            ReferenceInvalidationRecoveryConfirmationFrames = 5
        };
    }
}
