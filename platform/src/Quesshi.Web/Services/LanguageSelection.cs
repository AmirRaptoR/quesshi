using Quesshi.Web.Models;
using Microsoft.JSInterop;

namespace Quesshi.Web.Services;

public sealed class LanguageSelection(AppConfig config, Translator translator, IJSRuntime js)
{
    public string CurrentCode { get; private set; } = string.Empty;

    public static string Choose(TenantConfig config, string? saved, IEnumerable<string>? browserLanguages)
    {
        var offered = config.Languages.Select(language => language.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(saved) && offered.Contains(saved)) return config.Languages.First(language => string.Equals(language.Code, saved, StringComparison.OrdinalIgnoreCase)).Code;
        foreach (var browser in browserLanguages ?? [])
        {
            var match = config.Languages.FirstOrDefault(language => string.Equals(language.Code, browser, StringComparison.OrdinalIgnoreCase)
                || browser.StartsWith($"{language.Code}-", StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Code;
        }
        return config.Languages.FirstOrDefault(language => string.Equals(language.Code, config.DefaultLanguage, StringComparison.OrdinalIgnoreCase))?.Code
            ?? config.Languages.First().Code;
    }

    public async Task InitializeAsync()
    {
        var saved = await js.InvokeAsync<string?>("quesshi.get", "quesshi.lang");
        var browserLanguages = await js.InvokeAsync<string[]>("quesshi.browserLanguages");
        await ApplyAsync(Choose(config.Current, saved, browserLanguages), persist: false);
    }

    /// <summary>Applies and persists an explicit language choice from the picker.</summary>
    public Task UseAsync(string code) => ApplyAsync(code, persist: true);

    private async Task ApplyAsync(string code, bool persist)
    {
        var language = config.Current.Languages.FirstOrDefault(candidate => string.Equals(candidate.Code, code, StringComparison.OrdinalIgnoreCase));
        if (language is null) return;
        CurrentCode = language.Code;
        await translator.UseAsync(language.Code);
        await js.InvokeVoidAsync("quesshi.applyLanguage", language.Code, language.Direction);
        if (persist) await js.InvokeVoidAsync("quesshi.setLanguage", language.Code);
    }
}
