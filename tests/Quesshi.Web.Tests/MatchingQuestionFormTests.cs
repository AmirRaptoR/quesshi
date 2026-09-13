using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class MatchingQuestionFormTests
{
    private static MatchingQuestionForm Valid() => new()
    {
        Lang = "en", MatchingCategoryId = "m-music", Prompt = "Name a favourite song?", Choices = { }
    };

    [Fact]
    public void Fixed_answers_are_trimmed_and_sent()
    {
        var form = new MatchingQuestionForm { Lang = "en", MatchingCategoryId = "m-music", Prompt = " Prompt " };
        form.Choices[0] = " One "; form.Choices[1] = "Two";

        var dto = form.ToDto();

        Assert.Equal("fixed", dto.AnswerSource);
        Assert.Equal(["One", "Two"], dto.Choices);
        Assert.Equal("Prompt", dto.Prompt);
    }

    [Fact]
    public void Participants_toggle_sends_no_choices_and_clears_them_when_toggled_back()
    {
        var form = new MatchingQuestionForm { MatchingCategoryId = "m-music", Prompt = "Prompt" };
        form.Choices[0] = "One"; form.Choices[1] = "Two";

        form.UseParticipants = true;
        Assert.Empty(form.ToDto().Choices);
        Assert.Equal("participants", form.ToDto().AnswerSource);

        form.UseParticipants = false;
        Assert.Equal(2, form.Choices.Count);
        Assert.All(form.Choices, Assert.Empty);
    }

    [Fact]
    public void Choice_bounds_are_shared_and_enforced()
    {
        var form = new MatchingQuestionForm();
        form.AddChoice();
        Assert.Equal(MatchingQuestionForm.MinChoices + 1, form.Choices.Count);
        for (var i = form.Choices.Count; i < MatchingQuestionForm.MaxChoices; i++) form.AddChoice();
        form.AddChoice();
        Assert.Equal(MatchingQuestionForm.MaxChoices, form.Choices.Count);

        form.RemoveChoice(0);
        while (form.Choices.Count > MatchingQuestionForm.MinChoices) form.RemoveChoice(0);
        form.RemoveChoice(0);
        Assert.Equal(MatchingQuestionForm.MinChoices, form.Choices.Count);
    }

    [Fact]
    public void Blank_prompt_has_its_own_error()
    {
        var errors = new MatchingQuestionForm { MatchingCategoryId = "m-music", Prompt = " " }.Validate();
        Assert.Contains("blank_prompt", errors);
    }

    [Fact]
    public void Blank_choice_has_its_own_error()
    {
        var form = new MatchingQuestionForm { MatchingCategoryId = "m-music", Prompt = "Prompt" };
        form.Choices[0] = " ";
        Assert.Contains("blank_choice", form.Validate());
    }

    [Fact]
    public void Duplicate_choices_are_case_insensitive_and_trimmed()
    {
        var form = new MatchingQuestionForm { MatchingCategoryId = "m-music", Prompt = "Prompt" };
        form.Choices[0] = " One "; form.Choices[1] = "one";
        Assert.Contains("duplicate_choice", form.Validate());
    }

    [Fact]
    public void Too_few_and_too_many_choices_are_rejected()
    {
        var form = new MatchingQuestionForm { MatchingCategoryId = "m-music", Prompt = "Prompt" };
        form.Choices.RemoveAt(1);
        Assert.Contains("too_few_choices", form.Validate());
        while (form.Choices.Count < MatchingQuestionForm.MaxChoices + 1) form.Choices.Add("choice" + form.Choices.Count);
        Assert.Contains("too_many_choices", form.Validate());
    }

    [Fact]
    public void Missing_category_has_its_own_error()
    {
        var form = new MatchingQuestionForm { Prompt = "Prompt" };
        Assert.Contains("unknown_category", form.Validate());
    }

    [Fact]
    public void Subject_and_aspect_feed_the_wire_topic_fields()
    {
        var form = new MatchingQuestionForm { MatchingCategoryId = "m-music", Prompt = "Prompt", Subject = "Music", Aspect = "Jazz" };
        var dto = form.ToDto();
        Assert.Equal("Music", dto.Subject);
        Assert.Equal("Jazz", dto.Aspect);
    }

    [Fact]
    public void Existing_participant_question_has_no_stale_choices()
    {
        var loaded = MatchingQuestionForm.From(new MatchingQuestionDto("q", "en", "m-music", "Prompt", "participants", ["stale"], "approved", "admin", null, "music", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));
        Assert.True(loaded.UseParticipants);
        Assert.Empty(loaded.Choices);
    }
}
