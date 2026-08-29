namespace EitHost.Core.Hardware.Pnp;

public sealed class PnpInsertionMonitor
{
    private static readonly PnpDeviceSnapshot EmptySnapshot = new(DateTimeOffset.MinValue, []);
    private readonly IPnpDeviceScanner scanner;

    public PnpInsertionMonitor(IPnpDeviceScanner scanner)
    {
        this.scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
    }

    public PnpDeviceSnapshot? Baseline { get; private set; }

    public async Task<PnpDeviceSnapshot> InitializeAsync(CancellationToken cancellationToken = default)
    {
        Baseline = await scanner.CaptureAsync(cancellationToken).ConfigureAwait(false);
        return Baseline;
    }

    public async Task<PnpDeviceChange> DetectChangesAsync(CancellationToken cancellationToken = default)
    {
        var previous = Baseline ?? EmptySnapshot;
        var current = await scanner.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var change = PnpDeviceChange.FromSnapshots(previous, current);
        Baseline = current;
        return change;
    }

    public void AcceptBaseline(PnpDeviceSnapshot snapshot)
    {
        Baseline = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public void ClearBaseline()
    {
        Baseline = null;
    }
}
