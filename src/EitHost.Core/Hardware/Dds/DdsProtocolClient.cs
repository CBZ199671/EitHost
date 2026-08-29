namespace EitHost.Core.Hardware.Dds;

public sealed class DdsProtocolClient
{
    private readonly IDdsSerialTransport transport;
    private readonly DdsPacketBuilder packetBuilder;
    private DdsFirmwareCapabilities? capabilities;

    public DdsProtocolClient(IDdsSerialTransport transport, DdsPacketBuilder? packetBuilder = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.packetBuilder = packetBuilder ?? new DdsPacketBuilder();
    }

    public Task<DdsCommandResult> SetDacAsync(DdsDacSettings settings, CancellationToken cancellationToken = default)
    {
        return SendWithCapabilitiesAsync(packetBuilder.BuildSetDac(settings), null, cancellationToken);
    }

    public Task<DdsCommandResult> StopDacAsync(byte channel, CancellationToken cancellationToken = default)
    {
        return SendWithCapabilitiesAsync(packetBuilder.BuildStopDac(channel), null, cancellationToken);
    }

    public Task<DdsCommandResult> StartExcitationAsync(DdsExcitationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SendWithCapabilitiesAsync(packetBuilder.BuildStartExcitation(settings), settings, cancellationToken);
    }

    public Task<DdsCommandResult> StopExcitationAsync(CancellationToken cancellationToken = default)
    {
        return SendPacketAsync(packetBuilder.BuildStopExcitation(), null, null, cancellationToken);
    }

    public Task<DdsCommandResult> SetPgaAsync(byte gain, CancellationToken cancellationToken = default)
    {
        return SendWithCapabilitiesAsync(packetBuilder.BuildSetPga(gain), null, cancellationToken);
    }

    public async Task<DdsFirmwareCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        if (capabilities is not null)
        {
            return capabilities;
        }

        var packet = packetBuilder.BuildGetCapabilities();
        var responseBytes = await transport.ExchangeAsync(packet.ToArray(), cancellationToken).ConfigureAwait(false);
        var response = ValidateResponse(packet.Command, responseBytes);
        capabilities = DdsFirmwareCapabilities.Parse(response);
        capabilities.ValidateRequiredV2Contract();
        return capabilities;
    }

    public async Task<DdsScanStatus> GetScanStatusAsync(CancellationToken cancellationToken = default)
    {
        var verifiedCapabilities = await GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (!verifiedCapabilities.SupportsScanStatus)
        {
            throw new DdsProtocolException(
                $"DDS firmware {verifiedCapabilities.FirmwareVersion} does not advertise scan-status feature 0x{DdsProtocolConstants.ScanStatusFeatureFlag:X4}.");
        }

        var result = await SendPacketAsync(
            packetBuilder.BuildGetScanStatus(),
            verifiedCapabilities,
            null,
            cancellationToken).ConfigureAwait(false);
        return DdsScanStatus.Parse(result.Response ?? throw new DdsProtocolException(
            "DDS scan-status command returned no response."));
    }

    private async Task<DdsCommandResult> SendWithCapabilitiesAsync(
        DdsPacket packet,
        DdsExcitationSettings? excitationSettings,
        CancellationToken cancellationToken)
    {
        var verifiedCapabilities = await GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (excitationSettings is { ScanTimes: > 0 } && !verifiedCapabilities.SupportsScanStatus)
        {
            throw new DdsProtocolException(
                $"DDS firmware {verifiedCapabilities.FirmwareVersion} cannot run a finite scan safely because scan-status feature 0x{DdsProtocolConstants.ScanStatusFeatureFlag:X4} is absent.");
        }

        return await SendPacketAsync(packet, verifiedCapabilities, excitationSettings, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DdsCommandResult> SendPacketAsync(
        DdsPacket packet,
        DdsFirmwareCapabilities? verifiedCapabilities,
        DdsExcitationSettings? excitationSettings,
        CancellationToken cancellationToken)
    {
        var bytes = packet.ToArray();
        var responseBytes = await transport.ExchangeAsync(bytes, cancellationToken).ConfigureAwait(false);
        var response = ValidateResponse(packet.Command, responseBytes);
        ValidateSuccessPayload(packet, response);
        DdsExecutionReceipt? receipt = null;
        if (packet.Command == DdsCommand.StartExcitation)
        {
            if (verifiedCapabilities is null || excitationSettings is null)
            {
                throw new DdsProtocolException("DDS start ACK cannot be validated without firmware capabilities and settings.");
            }

            receipt = DdsExecutionReceipt.Parse(response, verifiedCapabilities);
            ValidateExecutionReceipt(receipt, excitationSettings);
        }

        return new DdsCommandResult(
            packet.Command,
            bytes,
            packet.Hex,
            DateTimeOffset.UtcNow,
            response,
            verifiedCapabilities,
            receipt);
    }

    private static DdsResponseFrame ValidateResponse(DdsCommand expectedCommand, ReadOnlySpan<byte> responseBytes)
    {
        var response = DdsResponseFrame.Parse(responseBytes);
        if (response.ProtocolVersion != DdsProtocolConstants.ProtocolVersion)
        {
            throw new DdsProtocolException(
                $"DDS firmware protocol v{response.ProtocolVersion} is incompatible; v{DdsProtocolConstants.ProtocolVersion} is required.");
        }

        if (response.Command != expectedCommand)
        {
            throw new DdsProtocolException(
                $"DDS ACK command {response.Command} does not match request {expectedCommand}.");
        }

        if (response.Status != DdsResponseStatus.Ok)
        {
            throw new DdsProtocolException(
                $"DDS command {expectedCommand} failed with firmware status {response.Status} (0x{(byte)response.Status:X2}).");
        }

        return response;
    }

    private static void ValidateSuccessPayload(DdsPacket packet, DdsResponseFrame response)
    {
        var expectedLength = packet.Command switch
        {
            DdsCommand.SetDac or DdsCommand.StopDac or DdsCommand.StopExcitation => 0,
            DdsCommand.SetPga => 1,
            DdsCommand.StartExcitation => 17,
            DdsCommand.GetCapabilities => 20,
            DdsCommand.GetScanStatus => 11,
            _ => throw new DdsProtocolException($"DDS command {packet.Command} has no v2 ACK payload contract.")
        };
        if (response.Payload.Count != expectedLength)
        {
            throw new DdsProtocolException(
                $"DDS command {packet.Command} ACK payload length {response.Payload.Count} does not match expected {expectedLength}.");
        }

        if (packet.Command == DdsCommand.SetPga && response.Payload[0] != packet.Payload[0])
        {
            throw new DdsProtocolException(
                $"DDS SetPga ACK echo {response.Payload[0]} does not match requested gain {packet.Payload[0]}.");
        }
    }

    private static void ValidateExecutionReceipt(
        DdsExecutionReceipt receipt,
        DdsExcitationSettings settings)
    {
        var expectedTimeUs = settings.CalculateTimeUs();
        var expectedEffectiveNs = checked((uint)Math.Round(
            receipt.TimerTicks * 1_000_000_000.0 / receipt.TimerClockHz,
            MidpointRounding.AwayFromZero));
        if (receipt.RequestedTimeUs != expectedTimeUs ||
            receipt.ScanTimes != checked((uint)settings.ScanTimes) ||
            receipt.Mode != settings.Mode ||
            receipt.TimerTicks == 0 ||
            receipt.EffectiveTimeNs != expectedEffectiveNs ||
            receipt.SwitchGuardMinimumUs < 2)
        {
            throw new DdsProtocolException(
                $"DDS start ACK does not match request: requested={expectedTimeUs}us/{settings.Mode}/{settings.ScanTimes}, " +
                $"ack={receipt.RequestedTimeUs}us/{receipt.Mode}/{receipt.ScanTimes}, ticks={receipt.TimerTicks}, " +
                $"effective={receipt.EffectiveTimeNs}ns, minimum_guard={receipt.SwitchGuardMinimumUs}us.");
        }
    }
}
