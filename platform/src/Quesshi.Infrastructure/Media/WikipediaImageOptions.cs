namespace Quesshi.Infrastructure.Media;

/// <summary>Bounds Wikipedia lead-image downloads and identifies the application to Wikimedia.</summary>
public sealed class WikipediaImageOptions
{
    public int Width { get; set; } = 800;

    public int MaxBytes { get; set; } = 3 * 1024 * 1024;

    public string UserAgent { get; set; } = "Quesshi/1.0 (https://github.com/AmirRaptoR/quesshi) dotnet-httpclient";
}
