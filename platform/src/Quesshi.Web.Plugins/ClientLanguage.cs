namespace Quesshi.Web.Plugins;

/// <summary>The language the shell is showing: its code, its text direction and how numerals are written in it.</summary>
public interface IClientLanguageInfo
{
    string Code { get; }

    /// <summary>"ltr" or "rtl".</summary>
    string Direction { get; }

    string Num(object? value);
}

/// <summary>Translated text with numeral formatting, for plugin components that compose sentences.</summary>
public sealed class ClientWords(IClientTranslator translator, IClientLanguageInfo language)
{
    public string this[string key] => translator[key];
    public string Format(string key, params object[] args) => string.Format(translator[key], args);
    public string Num(object? value) => language.Num(value);
}
