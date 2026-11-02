using Quesshi.Web.Plugins;

namespace Quesshi.Web.Services;

/// <summary>The shell's current language for plugin components: the translator's code and the tenant's direction for it.</summary>
public sealed class ClientLanguageInfo(Translator translator, AppConfig config) : IClientLanguageInfo
{
    public string Code => translator.Lang;

    public string Direction =>
        config.Current?.Languages.FirstOrDefault(language => string.Equals(language.Code, translator.Lang, StringComparison.OrdinalIgnoreCase))?.Direction
        ?? "ltr";

    public string Num(object? value) => translator.Num(value);
}
