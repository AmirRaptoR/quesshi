using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>
/// Looks up user-facing text by key. Nothing in the codebase should contain a translated string
/// literal; everything a person reads comes from a translation file through here.
/// </summary>
public interface ITranslator
{
    string Get(Language lang, string key);
}
