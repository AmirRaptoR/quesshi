using System.Text.Json;
using Microsoft.Extensions.Logging;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Localisation;

/// <summary>
/// Reads flat key/value JSON files, one per language, from a folder. Flat JSON rather than .resx
/// because these files are edited by translators, not by Visual Studio.
/// </summary>
public sealed class JsonFileTranslator : ITranslator
{
    private readonly Dictionary<Language, Dictionary<string, string>> _byLanguage = [];
    private readonly ILogger<JsonFileTranslator> _logger;

    public JsonFileTranslator(string folder, ILogger<JsonFileTranslator> logger)
    {
        _logger = logger;

        foreach (var lang in Enum.GetValues<Language>())
        {
            var path = Path.Combine(folder, $"{lang.ToString().ToLowerInvariant()}.json");
            _byLanguage[lang] = Load(path);
        }
    }

    public string Get(Language lang, string key)
    {
        if (_byLanguage.TryGetValue(lang, out var table) && table.TryGetValue(key, out var value)) return value;

        // A missing key is a content bug, not a crash: show the key so it is obvious in the UI.
        _logger.LogWarning("Missing translation {Key} for {Language}", key, lang);
        return key;
    }

    private Dictionary<string, string> Load(string path)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("Translation file {Path} is missing", path);
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Translation file {Path} is not valid JSON", path);
            return [];
        }
    }
}
