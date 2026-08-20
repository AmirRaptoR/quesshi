namespace Quesshi.Domain;

public sealed record MediaRef(MediaKind Kind, string Url, string? Attribution = null)
{
    public static readonly MediaRef None = new(MediaKind.None, string.Empty);
}
