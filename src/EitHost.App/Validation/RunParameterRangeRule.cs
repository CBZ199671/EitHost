using System.Globalization;
using System.Windows.Controls;

namespace EitHost.App.Validation;

/// <summary>
/// Validates operator-entered run parameters before WPF attempts source conversion, so a
/// malformed or out-of-range entry is reported inline instead of silently keeping the previous
/// value until the domain layer rejects it at start time.
/// </summary>
public sealed class RunParameterRangeRule : ValidationRule
{
    public double Minimum { get; set; } = double.NegativeInfinity;

    public double Maximum { get; set; } = double.PositiveInfinity;

    public bool MinimumInclusive { get; set; } = true;

    public bool MaximumInclusive { get; set; } = true;

    public bool AllowDecimal { get; set; }

    public override ValidationResult Validate(object? value, CultureInfo cultureInfo)
    {
        var text = value as string ?? value?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ValidationResult(false, $"不能为空；{DescribeExpectation()}。");
        }

        var trimmed = text.Trim();
        if (!TryParse(trimmed, cultureInfo, out var parsed))
        {
            return new ValidationResult(false, $"“{trimmed}”不是有效数值；{DescribeExpectation()}。");
        }

        if (double.IsNaN(parsed) || double.IsInfinity(parsed))
        {
            return new ValidationResult(false, $"数值必须有限；{DescribeExpectation()}。");
        }

        if (MinimumInclusive ? parsed < Minimum : parsed <= Minimum)
        {
            return new ValidationResult(false, $"数值过小；{DescribeExpectation()}。");
        }

        if (MaximumInclusive ? parsed > Maximum : parsed >= Maximum)
        {
            return new ValidationResult(false, $"数值过大；{DescribeExpectation()}。");
        }

        return ValidationResult.ValidResult;
    }

    private bool TryParse(string text, CultureInfo cultureInfo, out double parsed)
    {
        var culture = cultureInfo ?? CultureInfo.CurrentCulture;
        if (AllowDecimal)
        {
            return double.TryParse(text, NumberStyles.Float, culture, out parsed)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
        }

        parsed = 0;
        if (!long.TryParse(text, NumberStyles.Integer, culture, out var integer)
            && !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
        {
            return false;
        }

        parsed = integer;
        return true;
    }

    private string DescribeExpectation()
    {
        var kind = AllowDecimal ? "小数" : "整数";
        var hasMinimum = !double.IsNegativeInfinity(Minimum);
        var hasMaximum = !double.IsPositiveInfinity(Maximum);
        if (!hasMinimum && !hasMaximum)
        {
            return $"请输入{kind}";
        }

        if (hasMinimum && !hasMaximum)
        {
            return MinimumInclusive
                ? $"请输入不小于 {Format(Minimum)} 的{kind}"
                : $"请输入大于 {Format(Minimum)} 的{kind}";
        }

        if (!hasMinimum && hasMaximum)
        {
            return MaximumInclusive
                ? $"请输入不大于 {Format(Maximum)} 的{kind}"
                : $"请输入小于 {Format(Maximum)} 的{kind}";
        }

        var lower = MinimumInclusive ? $"{Format(Minimum)}" : $"大于 {Format(Minimum)}";
        var upper = MaximumInclusive ? $"{Format(Maximum)}" : $"小于 {Format(Maximum)}";
        return $"请输入 {lower} ~ {upper} 的{kind}";
    }

    private string Format(double bound)
    {
        return AllowDecimal
            ? bound.ToString("0.####", CultureInfo.CurrentCulture)
            : bound.ToString("0", CultureInfo.CurrentCulture);
    }
}
