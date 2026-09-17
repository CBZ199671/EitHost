namespace EitHost.App.Localization;

/// <summary>
/// Process-wide view of the active UI language so text produced outside the
/// visual tree (native dialogs, file pickers) can follow the same selection.
/// </summary>
internal static class UiLanguageContext
{
    internal static UiLanguage Current { get; private set; } = UiLanguage.SimplifiedChinese;

    internal static void Set(UiLanguage language)
    {
        Current = language;
    }

    internal static string Localize(string text)
    {
        return Current == UiLanguage.English ? EnglishUiText.Translate(text) : text;
    }
}
