using EitHost.Core.Reconstruction;

namespace EitHost.App.ViewModels.Workspaces;

internal sealed record RealtimeBaseMeasurementWeights(double[] Values, string PolicyVersion)
{
    internal static RealtimeBaseMeasurementWeights Resolve(
        bool provisionalReference,
        bool compensationEnabled,
        double[]? activeWeights,
        string? activePolicy)
    {
        if (activeWeights is not null)
            return new(activeWeights.ToArray(), activePolicy ?? "contact-policy-unrecorded");

        var reason = provisionalReference ? "provisional-preview-no-contact-compensation"
            : !compensationEnabled ? "contact-compensation-disabled"
            : "contact-diagnostics-not-ready";
        return new(
            Enumerable.Repeat(1.0, RealtimeReconstructionRequest.BoundaryVoltageCount).ToArray(),
            $"all-one-v1:{reason}");
    }
}
