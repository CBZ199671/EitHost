using EitHost.Core.Hardware.Pnp;

namespace EitHost.Core.Pairing;

public sealed class ManualPairingSession
{
    private readonly List<EitSetPairing> pairings = [];

    public IReadOnlyList<EitSetPairing> Pairings => pairings;

    public string SuggestNextLabel()
    {
        return $"EIT-{pairings.Count + 1:00}";
    }

    public EitSetPairing Bind(
        string label,
        int usb2070DeviceNumber,
        PnpDeviceCandidate usb2070Candidate,
        PnpDeviceCandidate ddsSerialCandidate)
    {
        var pairing = new EitSetPairing(
            label,
            usb2070DeviceNumber,
            usb2070Candidate,
            ddsSerialCandidate,
            DateTimeOffset.UtcNow);

        if (pairings.Any(existing => string.Equals(existing.Label, pairing.Label, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Device label '{pairing.Label}' already exists in this pairing session.");
        }

        if (pairings.Any(existing => SameCandidate(existing.Usb2070Candidate, pairing.Usb2070Candidate)))
        {
            throw new InvalidOperationException("The selected USB2070 candidate is already paired.");
        }

        if (pairings.Any(existing => existing.Usb2070DeviceNumber == pairing.Usb2070DeviceNumber))
        {
            throw new InvalidOperationException($"USB2070 device #{pairing.Usb2070DeviceNumber} is already paired.");
        }

        if (pairings.Any(existing => SameCandidate(existing.DdsSerialCandidate, pairing.DdsSerialCandidate)))
        {
            throw new InvalidOperationException("The selected DDS serial candidate is already paired.");
        }

        pairings.Add(pairing);
        return pairing;
    }

    public void Clear()
    {
        pairings.Clear();
    }

    public void ReplaceAll(IEnumerable<EitSetPairing> replacementPairings)
    {
        ArgumentNullException.ThrowIfNull(replacementPairings);
        var replacements = replacementPairings.ToArray();
        for (var index = 0; index < replacements.Length; index++)
        {
            var pairing = replacements[index];
            if (replacements.Take(index).Any(existing => string.Equals(existing.Label, pairing.Label, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Device label '{pairing.Label}' already exists in this pairing session.");
            }

            if (replacements.Take(index).Any(existing => SameCandidate(existing.Usb2070Candidate, pairing.Usb2070Candidate)))
            {
                throw new InvalidOperationException("The selected USB2070 candidate is already paired.");
            }

            if (replacements.Take(index).Any(existing => existing.Usb2070DeviceNumber == pairing.Usb2070DeviceNumber))
            {
                throw new InvalidOperationException($"USB2070 device #{pairing.Usb2070DeviceNumber} is already paired.");
            }

            if (replacements.Take(index).Any(existing => SameCandidate(existing.DdsSerialCandidate, pairing.DdsSerialCandidate)))
            {
                throw new InvalidOperationException("The selected DDS serial candidate is already paired.");
            }
        }

        pairings.Clear();
        pairings.AddRange(replacements);
    }

    private static bool SameCandidate(PnpDeviceCandidate left, PnpDeviceCandidate right)
    {
        return string.Equals(left.IdentityKey, right.IdentityKey, StringComparison.OrdinalIgnoreCase);
    }
}
