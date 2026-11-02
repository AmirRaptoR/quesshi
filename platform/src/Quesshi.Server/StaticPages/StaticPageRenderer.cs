using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;

namespace Quesshi.Server.StaticPages;

/// <summary>Renders a Razor component to a complete static HTML document on the server.</summary>
public sealed class StaticPageRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
{
    public async Task<string> RenderAsync<TComponent>(StaticPageModel model, IReadOnlyDictionary<string, object?>? extra = null) where TComponent : IComponent
    {
        await using var renderer = new HtmlRenderer(services, loggerFactory);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<TComponent>(
                ParameterView.FromDictionary(Parameters(model, extra)));
            return output.ToHtmlString();
        });
    }

    private static Dictionary<string, object?> Parameters(StaticPageModel model, IReadOnlyDictionary<string, object?>? extra)
    {
        var parameters = new Dictionary<string, object?> { ["Page"] = model };
        if (extra is not null) foreach (var (name, value) in extra) parameters[name] = value;
        return parameters;
    }
}
