namespace EitHost.Core.Reconstruction;

public sealed record WslPyEidorsReconstructionOptions(
    string DistroName = "Ubuntu-22.04",
    string BackendRepositoryPath = "",
    string? ExchangeDirectory = null,
    string BackendProfile = "",
    bool UseNixDevelop = false,
    string NixDevelopProfile = "",
    string WorkerExecutable = "eit-backend-worker",
    string WorkerArguments = "serve",
    string? WorkerLaunchCommand = null,
    string? DoctorCommand = null)
{
    public string ResolveExchangeDirectory()
    {
        return string.IsNullOrWhiteSpace(ExchangeDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "Data", ".exchange", "pyeidors")
            : Path.GetFullPath(ExchangeDirectory);
    }

    public string ResolveBackendRepositoryPath()
    {
        if (string.IsNullOrWhiteSpace(BackendRepositoryPath))
        {
            throw new InvalidOperationException("PyEIDORS backend repository path is empty.");
        }

        var trimmed = BackendRepositoryPath.Trim();
        return Path.IsPathFullyQualified(trimmed)
            ? WslPathMapper.ToWslPath(trimmed)
            : trimmed.Replace('\\', '/');
    }
}
