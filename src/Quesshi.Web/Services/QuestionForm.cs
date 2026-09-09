using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// The admin question editor's state: one flat object the form binds to, and the two conversions
/// between it and the wire.
///
/// <para>
/// It holds every kind's fields at once, and that is deliberate. An admin trying a question as a
/// sort and then as a map — which is a normal thing to do while writing one — would otherwise lose
/// their four items the moment they touched the kind switch. Nothing is thrown away while the form
/// is open; <see cref="ToDto"/> is what decides, at save time, which fields this kind is actually
/// allowed to send. That keeps the "a choice question must carry no map target" rule in one place
/// on this side, rather than spread across every handler that can change a field.
/// </para>
/// </summary>
public sealed class QuestionForm
{
    public string? Id { get; set; }
    public string Lang { get; set; } = "fa";
    public string CategoryId { get; set; } = "";
    public int Level { get; set; } = 1;
    public string Prompt { get; set; } = "";
    public List<string> Choices { get; set; } = ["", "", "", ""];
    public int CorrectIndex { get; set; }
    public string? Explanation { get; set; }
    public string? MediaKind { get; set; }
    public string? MediaUrl { get; set; }
    public string Status { get; set; } = "pending";

    /// <summary><c>"choice"</c>, <c>"sort"</c> or <c>"map"</c>.</summary>
    public string Kind { get; set; } = "choice";

    /// <summary><c>"country"</c> or <c>"city"</c>. Only read for a map question.</summary>
    public string TargetShape { get; set; } = "country";

    public string? CountryCode { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>
    /// A city target's tolerance. Starts at 150 km — the middle of the range the spec calls normal,
    /// where 50 is hard and 300 gentle — because a form that opened at either bound would be quietly
    /// recommending it.
    /// </summary>
    public double RadiusKm { get; set; } = MapTolerance.DefaultKm;

    /// <summary><c>"blank"</c> or <c>"borders"</c>.</summary>
    public string BaseLayer { get; set; } = "borders";

    public bool IsMap => Kind == "map";
    public bool IsSort => Kind == "sort";
    public bool IsCity => IsMap && TargetShape == "city";

    /// <summary>
    /// The target as the map component speaks about it. One type for the author's pick and the
    /// player's pick, so the authoring tool and the game agree about where a place is by
    /// construction rather than by two conversions being kept in step.
    /// </summary>
    public MapPick? Pick => TargetShape == "city"
        ? Latitude is { } lat && Longitude is { } lon ? MapPick.Point(lat, lon) : null
        : string.IsNullOrWhiteSpace(CountryCode) ? null : MapPick.Country(CountryCode);

    /// <summary>Records a click on the map. The shape decides which half of the target it sets, so a
    /// stray tap can never leave a country code and a coordinate pair both filled in.</summary>
    public void Apply(MapPick pick)
    {
        if (pick.IsCountry)
        {
            CountryCode = pick.CountryCode;
            Latitude = null;
            Longitude = null;
            return;
        }

        Latitude = pick.Latitude;
        Longitude = pick.Longitude;
        CountryCode = null;
    }

    public static QuestionForm From(AdminQuestionDto q) => new()
    {
        Id = q.Id, Lang = q.Lang, CategoryId = q.CategoryId, Level = q.Level, Prompt = q.Prompt,
        Choices = q.Choices.Count == 4 ? [.. q.Choices] : ["", "", "", ""],
        CorrectIndex = q.CorrectIndex, Explanation = q.Explanation,
        MediaKind = q.Media?.Kind, MediaUrl = q.Media?.Url, Status = q.Status,
        Kind = q.Kind,
        TargetShape = q.Target?.Shape ?? "country",
        CountryCode = q.Target?.CountryCode,
        Latitude = q.Target?.Latitude,
        Longitude = q.Target?.Longitude,
        RadiusKm = q.Target?.RadiusKm ?? MapTolerance.DefaultKm,
        BaseLayer = q.BaseLayer ?? "borders"
    };

    /// <summary>
    /// What actually gets posted. A map question sends no choices and no correct index, and the
    /// other two send no target and no base layer — the server enforces all four of those rules and
    /// says which one was broken, but a form that knowingly sent a rejectable body would be asking
    /// the admin to fix something they never typed.
    /// </summary>
    public SaveQuestionDto ToDto() => new(Id, Lang, CategoryId, Level, Prompt.Trim(),
        IsMap ? [] : [.. Choices.Select(c => c.Trim())],
        IsMap || IsSort ? 0 : CorrectIndex,
        Explanation, MediaKind, MediaUrl, Status,
        Kind,
        IsMap ? new MapTargetDto(TargetShape, CountryCode, Latitude, Longitude, RadiusKm) : null,
        IsMap ? BaseLayer : null);
}
