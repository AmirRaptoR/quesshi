using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.JSInterop;

namespace Quesshi.Web.Services;

/// <summary>Single client HTTP boundary for every platform API request.</summary>
public sealed class ClientHttp(HttpClient http, IClientReloader reloader)
{
    private readonly HashSet<string> reloadedBuilds = new(StringComparer.Ordinal);
    public static readonly string BuildId = typeof(ClientHttp).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ClientHttp).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public async Task<T?> GetFromJsonAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        request.Headers.TryAddWithoutValidation("X-Client-Build", BuildId);
        var response = await http.SendAsync(request, cancellationToken);
        if (IsOutdated(response) && reloadedBuilds.Add(BuildId)) await reloader.ReloadOnceAsync(BuildId);
        return response;
    }

    /// <summary>Opens a long-lived response: headers only, browser response streaming, never buffered or timed out as a whole.</summary>
    public async Task<HttpResponseMessage> OpenStreamAsync(string path, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Options.Set(new HttpRequestOptionsKey<bool>("WebAssemblyEnableStreamingResponse"), true);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("X-Client-Build", BuildId);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (IsOutdated(response) && reloadedBuilds.Add(BuildId)) await reloader.ReloadOnceAsync(BuildId);
        return response;
    }

    public static bool IsOutdated(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.UpgradeRequired
        || response.Headers.TryGetValues("X-Client-Status", out var values) && values.Contains("client_outdated", StringComparer.OrdinalIgnoreCase)
        || string.Equals(response.ReasonPhrase, "client_outdated", StringComparison.OrdinalIgnoreCase);
}

public interface IClientReloader
{
    Task ReloadOnceAsync(string buildId);
}

public sealed class JsClientReloader(IJSRuntime js) : IClientReloader
{
    public Task ReloadOnceAsync(string buildId) => js.InvokeVoidAsync("quesshi.reloadOnce", buildId).AsTask();
}
