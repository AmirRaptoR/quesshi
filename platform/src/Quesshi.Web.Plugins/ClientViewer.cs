namespace Quesshi.Web.Plugins;

/// <summary>What a plugin component needs to know about the person looking at it: language, text direction and account kind.</summary>
public interface IClientViewer
{
    string LanguageCode { get; }
    string Direction { get; }
    bool IsGuest { get; }

    /// <summary>Writes a number with the digits of the viewer's language.</summary>
    string Num(object? value);
}
