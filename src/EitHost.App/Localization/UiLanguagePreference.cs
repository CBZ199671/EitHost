using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace EitHost.App.Localization;

/// <summary>
/// Stores the user's UI language choice beside the other per-user settings and
/// resolves the first-run default from the Windows display language. The file
/// lives outside the install root so an in-place update keeps the choice.
/// </summary>
internal static class UiLanguagePreference
{
    internal const string ConfigFileName = "eithost.ui.json";

    private const string SimplifiedChineseToken = "zh-CN";
    private const string EnglishToken = "en-US";
    private const string ChineseIsoLanguage = "zh";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions SaveJsonOptions = new(JsonOptions)
    {
        WriteIndented = true
    };

    internal static string GetConfigPath(string? localAppDataDirectory = null)
    {
        var localAppDataRoot = string.IsNullOrWhiteSpace(localAppDataDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataDirectory;
        if (string.IsNullOrWhiteSpace(localAppDataRoot))
        {
            localAppDataRoot = AppContext.BaseDirectory;
        }

        return Path.Combine(localAppDataRoot, "EitHost", ConfigFileName);
    }

    /// <summary>
    /// First-run default: a Chinese Windows display language starts the workstation in
    /// Chinese, every other display language starts it in English.
    /// </summary>
    internal static UiLanguage ResolveSystemDefault(CultureInfo? uiCulture = null)
    {
        var culture = uiCulture ?? CultureInfo.CurrentUICulture;
        return string.Equals(
            culture.TwoLetterISOLanguageName,
            ChineseIsoLanguage,
            StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.SimplifiedChinese
            : UiLanguage.English;
    }

    /// <summary>
    /// Returns the stored choice, or null when none has been stored yet. A missing or
    /// damaged preference file is not an error: the caller falls back to the system default.
    /// </summary>
    internal static UiLanguage? Load(string? localAppDataDirectory = null)
    {
        try
        {
            var path = GetConfigPath(localAppDataDirectory);
            if (!File.Exists(path))
            {
                return null;
            }

            var config = JsonSerializer.Deserialize<UiLanguagePreferenceConfig>(
                File.ReadAllText(path, Encoding.UTF8),
                JsonOptions);
            return Parse(config?.Language);
        }
        catch (Exception)
        {
            // Broad on purpose: a stored preference is optional, the fallback is defined,
            // and this runs before the workstation can report anything to the user.
            return null;
        }
    }

    /// <summary>
    /// Never throws. It is the first thing application startup does, ahead of the
    /// dialogs that report startup failures, so nothing here may prevent the
    /// workstation from opening.
    /// </summary>
    internal static UiLanguage LoadOrSystemDefault(
        string? localAppDataDirectory = null,
        CultureInfo? uiCulture = null)
    {
        if (Load(localAppDataDirectory) is { } stored)
        {
            return stored;
        }

        try
        {
            return ResolveSystemDefault(uiCulture);
        }
        catch (Exception)
        {
            // An unreadable display language leaves the workstation on its original default.
            return UiLanguage.SimplifiedChinese;
        }
    }

    /// <summary>
    /// Writes the choice through a temporary file so an interrupted save cannot leave a
    /// truncated preference behind. Returns false when the choice could not be stored;
    /// the running session keeps the selected language either way.
    /// </summary>
    internal static bool Save(UiLanguage language, string? localAppDataDirectory = null)
    {
        string path;
        string? directory;
        string temporaryPath;
        try
        {
            path = GetConfigPath(localAppDataDirectory);
            directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }

            temporaryPath = Path.Combine(
                directory,
                $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);
            var content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new UiLanguagePreferenceConfig { Language = ToToken(language) },
                SaveJsonOptions));
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            // Storing the choice is best effort; the caller keeps the language applied
            // for this session rather than interrupting the user with a dialog.
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // A failed best-effort cleanup must not hide the original save result.
            }
        }
    }

    private static UiLanguage? Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmed = token.Trim();
        if (string.Equals(trimmed, EnglishToken, StringComparison.OrdinalIgnoreCase))
        {
            return UiLanguage.English;
        }

        if (string.Equals(trimmed, SimplifiedChineseToken, StringComparison.OrdinalIgnoreCase))
        {
            return UiLanguage.SimplifiedChinese;
        }

        // An unrecognised tag is still usable when it names a language the workstation has.
        try
        {
            var culture = CultureInfo.GetCultureInfo(trimmed);
            return string.Equals(
                culture.TwoLetterISOLanguageName,
                ChineseIsoLanguage,
                StringComparison.OrdinalIgnoreCase)
                ? UiLanguage.SimplifiedChinese
                : UiLanguage.English;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static string ToToken(UiLanguage language)
    {
        return language == UiLanguage.English ? EnglishToken : SimplifiedChineseToken;
    }

    private sealed class UiLanguagePreferenceConfig
    {
        public string? Language { get; set; }
    }
}
