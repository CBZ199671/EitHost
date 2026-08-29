namespace EitHost.Core.Hardware.Pnp;

public interface IPnpDeviceScanner
{
    Task<PnpDeviceSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
}
