using EitHost.Core.Demodulation;
using EitHost.Core.Diagnostics.ElectrodeContact;

namespace EitHost.App.ViewModels;

internal sealed record RealtimeTemporalSelection(
    RealtimeDemodulatedBlock Block,
    double[] Target,
    IReadOnlyList<double> MeasurementWeights,
    string WeightPolicyVersion,
    ElectrodeContactDiagnosticResult? ContactResult,
    EcdCwrWaveformTemplateDisplayPackage? TemplateDisplayPackage,
    EcdCwrTemporalDespikingResult? TemporalResult);
