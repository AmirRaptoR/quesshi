namespace Quesshi.Web.Plugins.Formats.Flashcards;

/// <summary>The client side of the <c>flashcards</c> format: choosing a pack to study, the card screen and the rating counts.</summary>
public sealed class FlashcardsClient : IFormatClient
{
    public const string FormatId = "flashcards";

    public string Id => FormatId;
    public Type Settings => typeof(FlashcardsSettings);
    public Type Screen => typeof(FlashcardsScreen);
    public Type Result => typeof(FlashcardsResult);
    public bool StartsFromSettings => true;
}
