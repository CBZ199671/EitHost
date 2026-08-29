namespace EitHost.Core.Domain;

public sealed record RunSession
{
    public RunSession(Guid id, string name, DateTimeOffset createdAt, IEnumerable<EitSet> deviceSets)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Run session id cannot be empty.", nameof(id));
        }

        Id = id;
        Name = RequireText(name);
        CreatedAt = createdAt;
        DeviceSets = ValidateDeviceSets(deviceSets);
    }

    public Guid Id { get; }

    public string Name { get; }

    public DateTimeOffset CreatedAt { get; }

    public IReadOnlyList<EitSet> DeviceSets { get; }

    public IReadOnlyList<DeviceRunMetadata> CreateDeviceMetadata()
    {
        return DeviceSets.Select(deviceSet => deviceSet.CreateRunMetadata()).ToArray();
    }

    private static IReadOnlyList<EitSet> ValidateDeviceSets(IEnumerable<EitSet> deviceSets)
    {
        ArgumentNullException.ThrowIfNull(deviceSets);

        var list = deviceSets.ToArray();
        if (list.Length == 0)
        {
            throw new ArgumentException("Run session requires at least one EIT set.", nameof(deviceSets));
        }

        var duplicateLabel = list
            .GroupBy(deviceSet => deviceSet.Label, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .FirstOrDefault();

        if (duplicateLabel is not null)
        {
            throw new ArgumentException($"Duplicate EIT set label: {duplicateLabel}", nameof(deviceSets));
        }

        return list;
    }

    private static string RequireText(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}
