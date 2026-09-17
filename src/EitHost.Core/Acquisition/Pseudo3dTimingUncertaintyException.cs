namespace EitHost.Core.Acquisition;

/// <summary>A rejected startup bracket; retry only after both devices confirm output off.</summary>
public sealed class Pseudo3dTimingUncertaintyException : IOException
{
    public const double MaximumMilliseconds = 50;

    public Pseudo3dTimingUncertaintyException(string setLabel, double adcInitializationMilliseconds,
        double startAcknowledgementMilliseconds)
        : base($"{setLabel} 启动时序不确定度 {adcInitializationMilliseconds / 2 + startAcknowledgementMilliseconds:0.0} ms 超过 50 ms，" +
            $"当前轮次作废（ADC 初始化 {adcInitializationMilliseconds:0.0} ms，启动应答 {startAcknowledgementMilliseconds:0.0} ms）。")
    {
        SetLabel = setLabel;
        AdcInitializationMilliseconds = adcInitializationMilliseconds;
        StartAcknowledgementMilliseconds = startAcknowledgementMilliseconds;
    }

    public string SetLabel { get; }
    public double AdcInitializationMilliseconds { get; }
    public double StartAcknowledgementMilliseconds { get; }
    public double UncertaintyMilliseconds => AdcInitializationMilliseconds / 2 + StartAcknowledgementMilliseconds;
}
