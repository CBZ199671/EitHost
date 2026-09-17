using PureHDF;

namespace EitHost.Core.Reconstruction;

// This index belongs to one open file/stream and never outlives its reader.
internal sealed class IndexedHdf5Group(IH5Group original)
{
    private readonly Dictionary<string, IH5Object> children = original.Children()
        .ToDictionary(child => child.Name, StringComparer.Ordinal);

    internal bool LinkExists(string path) => IsRootPath(path)
        ? children.ContainsKey(path.TrimStart('/'))
        : original.LinkExists(path);

    internal IH5Dataset Dataset(string path) => IsRootPath(path)
        ? (IH5Dataset)children[path.TrimStart('/')]
        : original.Dataset(path);

    internal bool AttributeExists(string name) => original.AttributeExists(name);
    internal IH5Attribute Attribute(string name) => original.Attribute(name);
    private static bool IsRootPath(string path) => !path.TrimStart('/').Contains('/');
}
