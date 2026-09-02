namespace EitHost.Core.Reconstruction;

public enum PyEidorsReconstructionFailureOrigin
{
    Backend,
    Frontend
}

public sealed class PyEidorsReconstructionException : Exception
{
    private PyEidorsReconstructionException(
        PyEidorsReconstructionFailureOrigin origin,
        string subsystem,
        string errorType,
        string detail,
        string message,
        string? backendTraceback = null,
        string? diagnostics = null,
        string? resultPath = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Origin = origin;
        Subsystem = subsystem;
        ErrorType = errorType;
        Detail = detail;
        BackendTraceback = backendTraceback;
        Diagnostics = diagnostics;
        ResultPath = resultPath;
    }

    public PyEidorsReconstructionFailureOrigin Origin { get; }

    public string Subsystem { get; }

    public string ErrorType { get; }

    public string Detail { get; }

    public string? BackendTraceback { get; }

    public string? Diagnostics { get; }

    public string? ResultPath { get; }

    public static PyEidorsReconstructionException FromBackend(
        string? errorType,
        string? detail,
        string? traceback = null,
        string? diagnostics = null)
    {
        var normalizedType = Normalize(errorType, "BackendWorkerError");
        var normalizedDetail = Normalize(detail, "后端未提供错误消息");
        return new PyEidorsReconstructionException(
            PyEidorsReconstructionFailureOrigin.Backend,
            "PyEIDORS worker/求解器",
            normalizedType,
            normalizedDetail,
            $"PyEIDORS 后端错误 [{normalizedType}]：{normalizedDetail}",
            NormalizeOptional(traceback),
            NormalizeOptional(diagnostics));
    }

    public static PyEidorsReconstructionException FromFrontendResult(
        string resultPath,
        Exception cause)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultPath);
        ArgumentNullException.ThrowIfNull(cause);
        var errorType = cause.GetType().Name;
        var detail = Normalize(cause.Message, "结果读取失败但未提供错误消息");
        return new PyEidorsReconstructionException(
            PyEidorsReconstructionFailureOrigin.Frontend,
            "HDF5/结果契约",
            errorType,
            detail,
            $"EitHost 前端结果读取错误 [{errorType}]：{detail}（文件：{resultPath}）",
            resultPath: resultPath,
            innerException: cause);
    }

    public static PyEidorsReconstructionException FromFrontendProcessing(
        string subsystem,
        Exception cause)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subsystem);
        ArgumentNullException.ThrowIfNull(cause);
        var errorType = cause.GetType().Name;
        var detail = Normalize(cause.Message, "前端后处理失败但未提供错误消息");
        return new PyEidorsReconstructionException(
            PyEidorsReconstructionFailureOrigin.Frontend,
            subsystem.Trim(),
            errorType,
            detail,
            $"EitHost 前端处理错误 [{errorType}]：{detail}",
            innerException: cause);
    }

    public static PyEidorsReconstructionException FromFrontendConfiguration(
        string configPath,
        string? errorType,
        string? detail,
        Exception? cause = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var normalizedType = Normalize(errorType, "BackendManifestError");
        var normalizedDetail = Normalize(detail, "后端配置错误但未提供错误消息");
        return new PyEidorsReconstructionException(
            PyEidorsReconstructionFailureOrigin.Frontend,
            "后端 manifest/配置",
            normalizedType,
            normalizedDetail,
            $"EitHost 前端配置错误 [{normalizedType}]：{normalizedDetail}（文件：{configPath}）",
            resultPath: configPath,
            innerException: cause);
    }

    public override string ToString()
    {
        var text = base.ToString();
        if (!string.IsNullOrWhiteSpace(BackendTraceback))
        {
            text += Environment.NewLine
                + "--- PyEIDORS backend traceback ---"
                + Environment.NewLine
                + BackendTraceback;
        }

        if (!string.IsNullOrWhiteSpace(Diagnostics))
        {
            text += Environment.NewLine
                + "--- PyEIDORS backend diagnostics ---"
                + Environment.NewLine
                + Diagnostics;
        }

        return text;
    }

    private static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
