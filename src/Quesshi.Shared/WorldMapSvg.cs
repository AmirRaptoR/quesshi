namespace Quesshi.Shared;

/// <summary>
/// The bundled world map's markup, read once out of this assembly's embedded copy of
/// <c>wwwroot/maps/world.svg</c>.
/// <para>
/// It is its own class because two things now need the file on the server —
/// <see cref="WorldMapCountries"/>, which wants the set of ISO codes, and
/// <see cref="WorldMapGeometry"/>, which wants the outlines themselves — and a second
/// <c>GetManifestResourceStream</c> call spelled out a second time is exactly how the two would
/// come to disagree about which resource name is the real one the day somebody renames it.
/// </para>
/// </summary>
internal static class WorldMapSvg
{
    private const string EmbeddedResourceName = "Quesshi.Shared.maps.world.svg";

    private static readonly Lazy<string> LazyText = new(Read);

    /// <summary>The whole file, as text. ~150 KB, loaded on first use and kept for the process.</summary>
    public static string Text => LazyText.Value;

    private static string Read()
    {
        var assembly = typeof(WorldMapSvg).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"'{EmbeddedResourceName}' is not embedded in {assembly.GetName().Name}. " +
                "Check the <EmbeddedResource> item in Quesshi.Shared.csproj still points at " +
                "wwwroot/maps/world.svg.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
