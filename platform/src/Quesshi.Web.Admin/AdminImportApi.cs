using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Quesshi.Web.Services;

namespace Quesshi.Web.Admin;

/// <summary>CSRF-protected multipart client for the editor import API.</summary>
public sealed class AdminImportApi(ClientHttp client)
{
    private const long MaxFileSize = 20 * 1024 * 1024;
    private string? antiforgeryToken;

    public Task<AdminResult<ImportReport>> DryRunAsync(ImportInputs inputs, CancellationToken cancellationToken = default) =>
        SendImportAsync(inputs, dryRun: true, cancellationToken);

    public Task<AdminResult<ImportReport>> ImportAsync(ImportInputs inputs, CancellationToken cancellationToken = default) =>
        SendImportAsync(inputs, dryRun: false, cancellationToken);

    private async Task<AdminResult<ImportReport>> SendImportAsync(ImportInputs inputs, bool dryRun, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (antiforgeryToken is null) await RefreshAntiforgeryAsync(cancellationToken);
        var response = await SendAsync(inputs, dryRun, cancellationToken);
        if (await IsStaleTokenAsync(response, cancellationToken))
        {
            response.Dispose();
            await RefreshAntiforgeryAsync(cancellationToken);
            response = await SendAsync(inputs, dryRun, cancellationToken);
        }

        using (response) return await ReadAsync(response, cancellationToken);
    }

    private async Task RefreshAntiforgeryAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/antiforgery");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<AntiforgeryToken>(cancellationToken: cancellationToken);
        antiforgeryToken = token?.Token ?? throw new InvalidOperationException("The server did not return an admin antiforgery token.");
    }

    private async Task<HttpResponseMessage> SendAsync(ImportInputs inputs, bool dryRun, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/import");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        request.Headers.TryAddWithoutValidation("X-CSRF-Admin", antiforgeryToken);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(inputs.Kind), "kind");
        form.Add(new StringContent(dryRun ? "true" : "false"), "dryRun");
        await AddFileAsync(form, inputs.File, "file", cancellationToken);
        if (inputs.MediaBundle is not null) await AddFileAsync(form, inputs.MediaBundle, "mediaBundle", cancellationToken);
        request.Content = form;
        return await client.SendAsync(request, cancellationToken);
    }

    private static Task AddFileAsync(MultipartFormDataContent form, IBrowserFile file, string field, CancellationToken cancellationToken)
    {
        var stream = file.OpenReadStream(MaxFileSize, cancellationToken);
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
        form.Add(content, field, file.Name);
        return Task.CompletedTask;
    }

    private static async Task<bool> IsStaleTokenAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.BadRequest) return false;
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        response.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        var error = JsonSerializer.Deserialize<ErrorResponse>(payload, JsonSerializerOptions.Web);
        return string.Equals(error?.Error, "invalid_antiforgery", StringComparison.Ordinal);
    }

    private static async Task<AdminResult<ImportReport>> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return new(true, await response.Content.ReadFromJsonAsync<ImportReport>(cancellationToken: cancellationToken), null);

        ErrorResponse? error = null;
        try { error = await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken: cancellationToken); }
        catch (JsonException) { }
        return new(false, null, "admin.import.failed", new AdminFailure(response.StatusCode, error?.Error));
    }

    private sealed record AntiforgeryToken(string? Token);
    private sealed record ErrorResponse(string? Error);
}

public sealed record ImportInputs(string Kind, IBrowserFile File, IBrowserFile? MediaBundle);
public sealed record ImportProblem(string Code, string Message);
public sealed record ImportRowReport(int Row, string Outcome, IReadOnlyList<ImportProblem>? Problems, Guid? ExistingQuestionId, int? ExistingRow, Guid? QuestionId);
public sealed record ImportWarning(string Code, string Message);
public sealed record ImportReport(IReadOnlyList<ImportRowReport> Rows, int Accepted, int Rejected, IReadOnlyList<ImportWarning> Warnings);
