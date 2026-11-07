using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace ValheimPlanner;

/// <summary>
/// Two layers of text:
///  * game names (pieces, materials, stations) come from the game's own localization table, in 36 languages;
///  * the tool's own UI goes through Godot's TranslationServer with gettext files in res://i18n
///    (English msgids are the source; i18n/fr.po etc. are the translations, i18n/messages.pot the template).
/// </summary>
public static class Loc
{
    public sealed record Lang(string Game, string Locale, string Native);

    // Same list and order as the game's localization table.
    public static readonly Lang[] Languages =
    {
        new("English", "en", "English"), new("French", "fr", "Français"), new("German", "de", "Deutsch"),
        new("Spanish", "es", "Español"), new("Italian", "it", "Italiano"), new("Portuguese_Brazilian", "pt_BR", "Português (Brasil)"),
        new("Portuguese_European", "pt_PT", "Português (Portugal)"), new("Dutch", "nl", "Nederlands"), new("Swedish", "sv", "Svenska"),
        new("Norwegian", "nb", "Norsk"), new("Danish", "da", "Dansk"), new("Finnish", "fi", "Suomi"), new("Icelandic", "is", "Íslenska"),
        new("Polish", "pl", "Polski"), new("Czech", "cs", "Čeština"), new("Slovak", "sk", "Slovenčina"), new("Hungarian", "hu", "Magyar"),
        new("Romanian", "ro", "Română"), new("Bulgarian", "bg", "Български"), new("Macedonian", "mk", "Македонски"),
        new("Serbian", "sr", "Српски"), new("Croatian", "hr", "Hrvatski"), new("Greek", "el", "Ελληνικά"), new("Russian", "ru", "Русский"),
        new("Ukrainian", "uk", "Українська"), new("Lithuanian", "lt", "Lietuvių"), new("Latvian", "lv", "Latviešu"),
        new("Turkish", "tr", "Türkçe"), new("Georgian", "ka", "ქართული"), new("Hindi", "hi", "हिन्दी"), new("Thai", "th", "ไทย"),
        new("Chinese", "zh_CN", "简体中文"), new("Chinese_Trad", "zh_TW", "繁體中文"), new("Japanese", "ja", "日本語"),
        new("Korean", "ko", "한국어"), new("Abenaki", "abe", "Wôbanakiôdwawôgan"),
    };

    public static Lang Current { get; private set; } = Languages[0];

    /// <summary>Number formatting (decimal comma etc.) for the current language.</summary>
    public static System.Globalization.CultureInfo Culture
    {
        get
        {
            try { return System.Globalization.CultureInfo.GetCultureInfo(Current.Locale.Replace('_', '-')); }
            catch (System.Globalization.CultureNotFoundException) { return System.Globalization.CultureInfo.InvariantCulture; }
        }
    }
    public static event Action? Changed;

    const string SettingsPath = "user://settings.json";

    /// <summary>Saved choice, else the system language, else English.</summary>
    public static void Init(string? forced = null)
    {
        Lang? pick = forced != null ? Find(forced) : null;
        if (pick == null && FileAccess.FileExists(SettingsPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(SettingsPath));
                if (doc.RootElement.TryGetProperty("language", out var l)) pick = Find(l.GetString() ?? "");
            }
            catch (Exception) { /* corrupt settings: fall back to detection */ }
        }
        pick ??= Find(OS.GetLocale()) ?? Find(OS.GetLocaleLanguage()) ?? Languages[0];
        Apply(pick, save: false);
    }

    public static Lang? Find(string codeOrName)
    {
        if (string.IsNullOrEmpty(codeOrName)) return null;
        string c = codeOrName.Replace('-', '_');
        return Languages.FirstOrDefault(l => l.Locale.Equals(c, StringComparison.OrdinalIgnoreCase) || l.Game.Equals(c, StringComparison.OrdinalIgnoreCase))
            ?? (c == "zh_HK" ? Find("zh_TW") : null)
            ?? (c.StartsWith("pt") ? Find(c.EndsWith("BR", StringComparison.OrdinalIgnoreCase) ? "pt_BR" : "pt_PT") : null)
            ?? (c is "no" or "nn" ? Find("nb") : null)
            ?? Languages.FirstOrDefault(l => l.Locale.Split('_')[0].Equals(c.Split('_')[0], StringComparison.OrdinalIgnoreCase));
    }

    public static void Apply(Lang lang, bool save = true)
    {
        Current = lang;
        TranslationServer.SetLocale(lang.Locale);
        if (save)
        {
            using var f = FileAccess.Open(SettingsPath, FileAccess.ModeFlags.Write);
            f?.StoreString(JsonSerializer.Serialize(new { language = lang.Locale }));
        }
        Changed?.Invoke();
    }

    /// <summary>Game name in the current language (English as fallback).</summary>
    public static string Game(Dictionary<string, string>? names, string fallback) =>
        names == null ? fallback
        : names.TryGetValue(Current.Game, out var s) ? s
        : names.TryGetValue("English", out var e) ? e : fallback;

    /// <summary>UI text: English msgid → current language.</summary>
    public static string T(string msgid) => TranslationServer.Translate(msgid).ToString();

    public static string F(string msgid, params object[] args) => string.Format(T(msgid), args);

    /// <summary>Plural form, e.g. N("{0} piece", "{0} pieces", n).</summary>
    public static string N(string singular, string plural, int n) =>
        string.Format(TranslationServer.TranslatePlural(singular, plural, n).ToString(), n);
}
