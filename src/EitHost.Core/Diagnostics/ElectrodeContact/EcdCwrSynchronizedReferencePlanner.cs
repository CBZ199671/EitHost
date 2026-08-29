namespace EitHost.Core.Diagnostics.ElectrodeContact;

public sealed record EcdCwrSynchronizedReferenceSelection(
    string SetLabel,
    EcdCwrReferenceWindow Window,
    double WindowSkewMilliseconds);

public sealed record EcdCwrSynchronizedReferencePlan(
    string ActionGroupId,
    DateTimeOffset CommonActionAt,
    IReadOnlyList<EcdCwrSynchronizedReferenceSelection> Selections);

public static class EcdCwrSynchronizedReferencePlanner
{
    public static EcdCwrSynchronizedReferencePlan Create(
        string actionGroupId,
        DateTimeOffset commonActionAt,
        IReadOnlyDictionary<string, IReadOnlyList<EcdCwrReferenceWindow>> windowsBySet)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionGroupId);
        ArgumentNullException.ThrowIfNull(windowsBySet);
        if (windowsBySet.Count < 2)
        {
            throw new InvalidOperationException("Synchronized reference planning requires at least two sets.");
        }

        var missingSets = new List<string>();
        var selections = new List<EcdCwrSynchronizedReferenceSelection>(windowsBySet.Count);
        foreach (var item in windowsBySet.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var window = item.Value
                .Where(candidate => candidate.EndedAt <= commonActionAt)
                .OrderByDescending(candidate => candidate.EffectiveReferenceAt)
                .ThenByDescending(candidate => candidate.EndedAt)
                .FirstOrDefault();
            if (window is null)
            {
                missingSets.Add(item.Key);
                continue;
            }

            selections.Add(new EcdCwrSynchronizedReferenceSelection(
                item.Key,
                window,
                (window.EffectiveReferenceAt - commonActionAt).TotalMilliseconds));
        }

        if (missingSets.Count > 0)
        {
            throw new InvalidOperationException(
                $"Sets without a complete high-quality window before the common action: {string.Join(", ", missingSets)}.");
        }

        return new EcdCwrSynchronizedReferencePlan(
            actionGroupId,
            commonActionAt,
            selections);
    }
}
