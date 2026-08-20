using Microsoft.AspNetCore.Components;

namespace Quesshi.Web.Services;

/// <summary>
/// Keeps a page's filters in the address bar, so a filtered view can be bookmarked, shared, and
/// survives a reload. Values equal to their default are left out, which keeps a plain URL plain.
/// </summary>
public static class QueryState
{
    /// <summary>Reads a string value, falling back to <paramref name="fallback"/> when absent.</summary>
    public static string Read(NavigationManager nav, string key, string fallback = "")
        => QueryValues.Read(nav.Uri, key) ?? fallback;

    public static int ReadInt(NavigationManager nav, string key, int fallback)
        => int.TryParse(QueryValues.Read(nav.Uri, key), out var value) ? value : fallback;

    /// <summary>
    /// Rewrites the address bar from the given values, dropping any that are null or empty.
    /// Replaces rather than pushes: changing a filter should not fill the back button with
    /// every keystroke of a search box.
    /// </summary>
    public static void Write(NavigationManager nav, IReadOnlyDictionary<string, string?> values)
    {
        var path = new Uri(nav.Uri).AbsolutePath;

        var query = string.Join('&', values
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));

        var target = query.Length == 0 ? path : $"{path}?{query}";

        // Navigating to where we already are would re-render for nothing.
        if (!string.Equals(new Uri(nav.Uri).PathAndQuery, target, StringComparison.Ordinal))
            nav.NavigateTo(target, forceLoad: false, replace: true);
    }
}
