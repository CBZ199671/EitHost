using System.Buffers.Binary;
using EitHost.Core.Hardware.Dds;

namespace EitHost.Core.Simulation;

public sealed class SimulatedDdsSerialTransport : IDdsSerialTransport
{
    private bool scanRunning;
    private uint targetCycles;

    public List<byte[]> Packets { get; } = [];

    public Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = packet.ToArray();
        Packets.Add(request);
        var command = (DdsCommand)request[1];
        return Task.FromResult(command switch
        {
            DdsCommand.GetCapabilities => BuildCapabilitiesResponse(),
            DdsCommand.StartExcitation => BuildStartResponseAndTrack(request),
            DdsCommand.StopExcitation => BuildStopResponse(),
            DdsCommand.GetScanStatus => BuildScanStatusResponse(),
            DdsCommand.SetPga => BuildResponse(command, DdsResponseStatus.Ok, [request[2]]),
            _ => BuildResponse(command, DdsResponseStatus.Ok, [])
        });
    }

    private static byte[] BuildCapabilitiesResponse()
    {
        Span<byte> payload = stackalloc byte[20];
        payload[0] = 1;
        payload[1] = 4;
        payload[2] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(
            payload[3..5],
            DdsProtocolConstants.RequiredFeatureFlags | DdsProtocolConstants.ScanStatusFeatureFlag);
        BinaryPrimitives.WriteUInt32BigEndian(payload[5..9], DdsProtocolConstants.TimerClockHz);
        BinaryPrimitives.WriteUInt32BigEndian(payload[9..13], DdsProtocolConstants.MinimumExcitationTimeUs);
        BinaryPrimitives.WriteUInt32BigEndian(payload[13..17], DdsProtocolConstants.MaximumExcitationTimeUs);
        payload[17] = 16;
        BinaryPrimitives.WriteUInt16BigEndian(payload[18..20], 2);
        return BuildResponse(DdsCommand.GetCapabilities, DdsResponseStatus.Ok, payload);
    }

    private static byte[] BuildStartResponse(ReadOnlySpan<byte> request)
    {
        var requestedUs = BinaryPrimitives.ReadUInt32BigEndian(request[3..7]);
        var ticks = checked((ushort)(((requestedUs / 625) * 576) + (((requestedUs % 625) * 576 + 312) / 625)));
        var effectiveNs = checked((uint)Math.Round(
            ticks * 1_000_000_000.0 / DdsProtocolConstants.TimerClockHz,
            MidpointRounding.AwayFromZero));
        Span<byte> payload = stackalloc byte[17];
        BinaryPrimitives.WriteUInt32BigEndian(payload[0..4], requestedUs);
        BinaryPrimitives.WriteUInt16BigEndian(payload[4..6], ticks);
        BinaryPrimitives.WriteUInt32BigEndian(payload[6..10], effectiveNs);
        request[7..11].CopyTo(payload[10..14]);
        payload[14] = request[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload[15..17], 2);
        return BuildResponse(DdsCommand.StartExcitation, DdsResponseStatus.Ok, payload);
    }

    private byte[] BuildStartResponseAndTrack(ReadOnlySpan<byte> request)
    {
        scanRunning = true;
        targetCycles = BinaryPrimitives.ReadUInt32BigEndian(request[7..11]);
        return BuildStartResponse(request);
    }

    private byte[] BuildStopResponse()
    {
        scanRunning = false;
        targetCycles = 0;
        return BuildResponse(DdsCommand.StopExcitation, DdsResponseStatus.Ok, []);
    }

    private byte[] BuildScanStatusResponse()
    {
        Span<byte> payload = stackalloc byte[11];
        payload[0] = scanRunning ? (byte)DdsScanState.Running : (byte)DdsScanState.Idle;
        payload[1] = scanRunning ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(payload[3..7], targetCycles);
        return BuildResponse(DdsCommand.GetScanStatus, DdsResponseStatus.Ok, payload);
    }

    private static byte[] BuildResponse(
        DdsCommand command,
        DdsResponseStatus status,
        ReadOnlySpan<byte> payload)
    {
        var response = new byte[payload.Length + DdsProtocolConstants.ResponseFrameOverhead];
        response[0] = DdsProtocolConstants.ResponseFrameHeader;
        response[1] = DdsProtocolConstants.ProtocolVersion;
        response[2] = (byte)command;
        response[3] = (byte)status;
        response[4] = checked((byte)payload.Length);
        payload.CopyTo(response.AsSpan(5));
        foreach (var value in response.AsSpan(0, response.Length - 1))
        {
            response[^1] ^= value;
        }

        return response;
    }
}
