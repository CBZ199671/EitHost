using System.Buffers.Binary;

namespace EitHost.Core.Hardware.Dds;

public enum DdsScanState : byte
{
    Idle = 0,
    Running = 1,
    Completed = 2
}

public sealed record DdsScanStatus(
    DdsScanState State,
    bool Running,
    byte CurrentStep,
    uint TargetCycles,
    uint CompletedCycles)
{
    public static DdsScanStatus Parse(DdsResponseFrame response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Command != DdsCommand.GetScanStatus || response.Payload.Count != 11)
        {
            throw new DdsProtocolException("DDS scan-status response has an invalid command or payload length.");
        }

        var payload = response.Payload.ToArray().AsSpan();
        if (payload[0] > (byte)DdsScanState.Completed || payload[1] > 1 || payload[2] >= 16)
        {
            throw new DdsProtocolException("DDS scan-status response contains an invalid state, running flag, or step.");
        }

        var result = new DdsScanStatus(
            (DdsScanState)payload[0],
            payload[1] != 0,
            payload[2],
            BinaryPrimitives.ReadUInt32BigEndian(payload[3..7]),
            BinaryPrimitives.ReadUInt32BigEndian(payload[7..11]));
        result.ValidateContract();
        return result;
    }

    private void ValidateContract()
    {
        if (Running != (State == DdsScanState.Running))
        {
            throw new DdsProtocolException("DDS scan-status state and running flag disagree.");
        }

        var valid = State switch
        {
            DdsScanState.Idle => CurrentStep == 0 && TargetCycles == 0 && CompletedCycles == 0,
            DdsScanState.Running => TargetCycles == 0 || CompletedCycles < TargetCycles,
            DdsScanState.Completed => CurrentStep == 15 && TargetCycles > 0 && CompletedCycles == TargetCycles,
            _ => false
        };
        if (!valid)
        {
            throw new DdsProtocolException(
                $"DDS scan-status counters violate the {State} contract: step={CurrentStep}, target={TargetCycles}, completed={CompletedCycles}.");
        }
    }
}
