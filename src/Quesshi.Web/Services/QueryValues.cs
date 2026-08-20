namespace Quesshi.Web.Services;

/// <summary>
/// Reads one value out of a query string. A whole package for this would be more dependency than
/// the four lines it replaces.
/// </summary>
public static class QueryValues
{
    public static string? Read(string uri, string key)
    {
        var question = uri.IndexOf('?');
        if (question < 0) return null;

        foreach (var pair in uri[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var name = equals < 0 ? pair : pair[..equals];

            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) continue;

            return equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..]);
        }

        return null;
    }
}
