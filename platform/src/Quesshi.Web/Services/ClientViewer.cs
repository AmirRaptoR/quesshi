using Quesshi.Web.Plugins;

namespace Quesshi.Web.Services;

/// <summary>The shell's language, tenant configuration and identity, as plugin components see them.</summary>
public sealed class ClientViewer(LanguageSelection language, AppConfig config, Translator translator, AuthState auth) : IClientViewer
{
    public string LanguageCode => language.CurrentCode;

    public string Direction => config.Current.Languages
        .FirstOrDefault(candidate => string.Equals(candidate.Code, language.CurrentCode, StringComparison.OrdinalIgnoreCase))?.Direction ?? "ltr";

    public bool IsGuest => auth.Player?.IsGuest ?? true;

    public string Num(object? value) => translator.Num(value);
}
