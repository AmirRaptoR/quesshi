namespace Quesshi.Infrastructure.Media;

public sealed class WikipediaImageOptions
{
    /// <summary>Where downloaded pictures are written. Served from the site root at <see cref="PublicPath"/>.</summary>
    public string StorageRoot { get; set; } = "wwwroot/media/sourced";
    public string PublicPath { get; set; } = "/media/sourced";

    /// <summary>Thumbnail width. Originals can be tens of megabytes, which no phone should download.</summary>
    public int Width { get; set; } = 800;

    /// <summary>A hard ceiling on what we will store, whatever the thumbnail service returns.</summary>
    public int MaxBytes { get; set; } = 3 * 1024 * 1024;

    /// <summary>
    /// Wikimedia asks for a descriptive agent that identifies the app and a contact. Set this to
    /// your own deployment's address before fetching anything at volume.
    /// </summary>
    public string UserAgent { get; set; } = "Quesshi/1.0 (https://github.com/AmirRaptoR/quesshi) dotnet-httpclient";
}
