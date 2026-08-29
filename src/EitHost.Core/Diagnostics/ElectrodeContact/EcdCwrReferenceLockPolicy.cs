namespace EitHost.Core.Diagnostics.ElectrodeContact;

public enum EcdCwrReferenceTrustStage
{
    None,
    Provisional,
    Formal
}

public enum EcdCwrReferenceLockAction
{
    None,
    LockProvisional,
    LockFormal,
    LockUserSelected
}

public static class EcdCwrReferenceLockPolicy
{
    public static EcdCwrReferenceLockAction Decide(
        EcdCwrReferenceTrustStage currentStage,
        int strictGreenFrameCount,
        bool stationarityCanLock,
        int minimumFrameCount = 100,
        bool userLockRequested = false)
    {
        if (minimumFrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumFrameCount));
        }

        if (strictGreenFrameCount < minimumFrameCount ||
            currentStage == EcdCwrReferenceTrustStage.Formal)
        {
            return EcdCwrReferenceLockAction.None;
        }

        if (userLockRequested)
        {
            return EcdCwrReferenceLockAction.LockUserSelected;
        }

        if (stationarityCanLock)
        {
            return EcdCwrReferenceLockAction.LockFormal;
        }

        return currentStage == EcdCwrReferenceTrustStage.None
            ? EcdCwrReferenceLockAction.LockProvisional
            : EcdCwrReferenceLockAction.None;
    }
}
