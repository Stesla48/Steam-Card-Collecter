namespace SteamCardCollector.App;

internal sealed record AppLanguage(string Code, string NativeName, string Prompt, string Continue)
{
    internal static readonly AppLanguage[] All =
    [
        new("en", "English", "Choose your language", "Continue"),
        new("tr", "Türkçe", "Dil seçin", "Devam"),
        new("de", "Deutsch", "Sprache wählen", "Weiter"),
        new("es", "Español", "Elige tu idioma", "Continuar"),
        new("fr", "Français", "Choisissez votre langue", "Continuer"),
        new("zh-Hans", "简体中文", "选择语言", "继续")
    ];

    internal static AppLanguage? Find(string? code) => All.FirstOrDefault(language => language.Code == code);
}
