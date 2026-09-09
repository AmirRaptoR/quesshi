using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// The admin editor's own half of the per-kind rules. The server is the authority and refuses
/// anything that breaks them, but a form that knowingly posted a body it knew would be refused
/// would be asking an admin to fix something they never typed — so the conversion to the wire is
/// where a kind's fields stop travelling.
/// </summary>
public class QuestionFormTests
{
    private static QuestionForm Filled() => new()
    {
        Lang = "en", CategoryId = "geography", Level = 2, Prompt = " Where is this? ",
        Choices = ["Tokyo", "Delhi", "Cairo", "Lima"], CorrectIndex = 2
    };

    [Fact]
    public void A_choice_question_sends_its_options_and_no_map_fields()
    {
        var dto = Filled().ToDto();

        Assert.Equal("choice", dto.Kind);
        Assert.Equal(4, dto.Choices.Count);
        Assert.Equal(2, dto.CorrectIndex);
        Assert.Null(dto.Target);
        Assert.Null(dto.BaseLayer);
        Assert.Equal("Where is this?", dto.Prompt);
    }

    /// <summary>A sorting question's answer is the order it is written in, so there is no index to
    /// send — whatever the radio buttons happened to be left on before the kind was switched.</summary>
    [Fact]
    public void A_sorting_question_sends_its_items_in_order_and_no_index()
    {
        var form = Filled();
        form.Kind = "sort";

        var dto = form.ToDto();

        Assert.Equal("sort", dto.Kind);
        Assert.Equal(["Tokyo", "Delhi", "Cairo", "Lima"], dto.Choices);
        Assert.Equal(0, dto.CorrectIndex);
        Assert.Null(dto.Target);
    }

    /// <summary>
    /// The case the validation table exists for, from the form's side: an admin who wrote four
    /// options and then made the question a map question must not post both.
    /// </summary>
    [Fact]
    public void A_map_question_sends_no_choices_even_when_some_were_typed()
    {
        var form = Filled();
        form.Kind = "map";
        form.CountryCode = "NL";

        var dto = form.ToDto();

        Assert.Empty(dto.Choices);
        Assert.Equal(0, dto.CorrectIndex);
        Assert.Equal("country", dto.Target!.Shape);
        Assert.Equal("NL", dto.Target.CountryCode);
        Assert.Equal("borders", dto.BaseLayer);
    }

    /// <summary>
    /// And the four options are still there when they change their mind back. Nothing is thrown away
    /// while the form is open — trying a question as one kind and then another is a normal thing to
    /// do while writing one.
    /// </summary>
    [Fact]
    public void Switching_kind_and_back_does_not_lose_what_was_typed()
    {
        var form = Filled();

        form.Kind = "map";
        _ = form.ToDto();
        form.Kind = "choice";

        Assert.Equal(["Tokyo", "Delhi", "Cairo", "Lima"], form.ToDto().Choices);
    }

    [Fact]
    public void A_city_target_carries_its_point_and_its_radius()
    {
        var form = Filled();
        form.Kind = "map";
        form.TargetShape = "city";
        form.Apply(MapPick.Point(52.37, 4.90));
        form.RadiusKm = 120;

        var target = form.ToDto().Target!;

        Assert.Equal("city", target.Shape);
        Assert.Equal(52.37, target.Latitude);
        Assert.Equal(4.90, target.Longitude);
        Assert.Equal(120, target.RadiusKm);
        Assert.Null(target.CountryCode);
    }

    /// <summary>
    /// Exactly one shape is populated, whatever order the taps came in. A form that kept both after
    /// the author changed their mind would leave the server deciding which half to believe.
    /// </summary>
    [Fact]
    public void A_pick_replaces_the_other_shape_rather_than_joining_it()
    {
        var form = new QuestionForm { Kind = "map", TargetShape = "city" };

        form.Apply(MapPick.Point(52.37, 4.90));
        form.Apply(MapPick.Country("nl"));

        Assert.Equal("NL", form.CountryCode);
        Assert.Null(form.Latitude);
        Assert.Null(form.Longitude);

        form.Apply(MapPick.Point(35.69, 51.39));

        Assert.Null(form.CountryCode);
        Assert.Equal(35.69, form.Latitude);
    }

    /// <summary>What the map component is handed: the target as a pick, or nothing to draw yet.</summary>
    [Fact]
    public void The_pick_follows_the_shape_being_edited()
    {
        var form = new QuestionForm { Kind = "map", CountryCode = "NL" };

        Assert.Equal("NL", form.Pick!.CountryCode);

        form.TargetShape = "city";
        Assert.Null(form.Pick);

        form.Apply(MapPick.Point(52.37, 4.90));
        Assert.True(form.Pick!.IsPoint);
    }

    [Fact]
    public void Editing_an_existing_map_question_loads_its_target()
    {
        var loaded = QuestionForm.From(new AdminQuestionDto("q1", "nl", "geography", 3, "Waar ligt dit?",
            [], 0, null, "approved", "admin", null, DateTimeOffset.UnixEpoch, 0, 0, 0, [],
            "map", new MapTargetDto("city", null, 52.37, 4.90, 300), "blank"));

        Assert.Equal("map", loaded.Kind);
        Assert.Equal("city", loaded.TargetShape);
        Assert.Equal(300, loaded.RadiusKm);
        Assert.Equal("blank", loaded.BaseLayer);

        // Four empty option boxes are waiting in case the kind is changed, rather than none at all —
        // a map question stores no choices, and the form still has four inputs to render.
        Assert.Equal(4, loaded.Choices.Count);
    }

    [Fact]
    public void Editing_an_existing_choice_question_is_unchanged()
    {
        var loaded = QuestionForm.From(new AdminQuestionDto("q2", "fa", "history", 1, "کدام؟",
            ["الف", "ب", "پ", "ت"], 3, "چون", "pending", "ai", null, DateTimeOffset.UnixEpoch, 0, 0, 0, []));

        Assert.Equal("choice", loaded.Kind);
        Assert.Equal(3, loaded.CorrectIndex);
        Assert.Null(loaded.ToDto().Target);
    }
}
