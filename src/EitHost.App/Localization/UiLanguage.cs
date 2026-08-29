using System.Globalization;

namespace EitHost.App.Localization;

internal enum UiLanguage
{
    SimplifiedChinese,
    English
}

internal static class UiLanguageExtensions
{
    internal static CultureInfo ToCulture(this UiLanguage language)
    {
        return CultureInfo.GetCultureInfo(language == UiLanguage.English ? "en-US" : "zh-CN");
    }
}
