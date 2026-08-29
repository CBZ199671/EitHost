namespace EitHost.App.ViewModels;

public sealed record RoiCurveMarker(
    double Left,
    double Top,
    double Size,
    string Tooltip);

public sealed record SelectionOption(string Label, string Value)
{
    public override string ToString()
    {
        return Label;
    }
}
