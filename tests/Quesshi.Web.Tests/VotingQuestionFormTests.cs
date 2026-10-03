using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class VotingQuestionFormTests
{
    private static VotingQuestionForm Valid() => new()
    {
        Lang = "en",
        CategoryId = "m-music",
        Prompt = "Name a favourite song?",
        Choices = { }
    };

    [Fact]
    public void Fixed_answers_are_trimmed_and_sent()
    {
        var form = new VotingQuestionForm { Lang = "en", CategoryId = "m-music", Prompt = " Prompt " };
        form.Choices[0] = " One "; form.Choices[1] = "Two";

        var dto = form.ToDto();

        Assert.Equal("fixed", dto.AnswerSource);
        Assert.Equal(["One", "Two"], dto.Choices);
        Assert.Equal("Prompt", dto.Prompt);
    }

    [Fact]
    public void Participants_toggle_sends_no_choices_and_clears_them_when_toggled_back()
    {
        var form = new VotingQuestionForm { CategoryId = "m-music", Prompt = "Prompt" };
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
        var form = new VotingQuestionForm();
        form.AddChoice();
        Assert.Equal(VotingQuestionForm.MinChoices + 1, form.Choices.Count);
        for (var i = form.Choices.Count; i < VotingQuestionForm.MaxChoices; i++) form.AddChoice();
        form.AddChoice();
        Assert.Equal(VotingQuestionForm.MaxChoices, form.Choices.Count);

        form.RemoveChoice(0);
        while (form.Choices.Count > VotingQuestionForm.MinChoices) form.RemoveChoice(0);
        form.RemoveChoice(0);
        Assert.Equal(VotingQuestionForm.MinChoices, form.Choices.Count);
    }

    [Fact]
    public void Blank_prompt_has_its_own_error()
    {
        var errors = new VotingQuestionForm { CategoryId = "m-music", Prompt = " " }.Validate();
        Assert.Contains("blank_prompt", errors);
    }

    [Fact]
    public void Blank_choice_has_its_own_error()
    {
        var form = new VotingQuestionForm { CategoryId = "m-music", Prompt = "Prompt" };
        form.Choices[0] = " ";
        Assert.Contains("blank_choice", form.Validate());
    }

    [Fact]
    public void Duplicate_choices_are_case_insensitive_and_trimmed()
    {
        var form = new VotingQuestionForm { CategoryId = "m-music", Prompt = "Prompt" };
        form.Choices[0] = " One "; form.Choices[1] = "one";
        Assert.Contains("duplicate_choice", form.Validate());
    }

    [Fact]
    public void Too_few_and_too_many_choices_are_rejected()
    {
        var form = new VotingQuestionForm { CategoryId = "m-music", Prompt = "Prompt" };
        form.Choices.RemoveAt(1);
        Assert.Contains("too_few_choices", form.Validate());
        while (form.Choices.Count < VotingQuestionForm.MaxChoices + 1) form.Choices.Add("choice" + form.Choices.Count);
        Assert.Contains("too_many_choices", form.Validate());
    }

    [Fact]
    public void Missing_category_is_allowed_for_uncategorized_voting_content()
    {
        var form = new VotingQuestionForm { Prompt = "Prompt" };
        Assert.DoesNotContain("unknown_category", form.Validate());
        Assert.Null(form.ToDto().CategoryId);
    }

    [Fact]
    public void Subject_and_aspect_feed_the_wire_topic_fields()
    {
        var form = new VotingQuestionForm { CategoryId = "m-music", Prompt = "Prompt", Subject = "Music", Aspect = "Jazz" };
        var dto = form.ToDto();
        Assert.Equal("Music", dto.Subject);
        Assert.Equal("Jazz", dto.Aspect);
    }

    [Fact]
    public void Existing_participant_question_has_no_stale_choices()
    {
        var loaded = VotingQuestionForm.From(new VotingQuestionDto("q", "en", "m-music", "Prompt", "participants", ["stale"], "approved", "admin", null, "music|jazz", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));
        Assert.True(loaded.UseParticipants);
        Assert.Empty(loaded.Choices);
        Assert.Equal("music", loaded.Subject);
        Assert.Equal("jazz", loaded.Aspect);
    }

    [Fact]
    public void Existing_topic_and_media_attribution_round_trip_without_loss()
    {
        var loaded = VotingQuestionForm.From(new VotingQuestionDto(
            "q", "en", "m-music", "Prompt", "fixed", ["one", "two"], "approved", "admin",
            new MediaDto("image", "/media/q.jpg", "Photo by Ada"), "jazz|guitar", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, 0));

        var dto = loaded.ToDto();

        Assert.Equal("jazz", dto.Subject);
        Assert.Equal("guitar", dto.Aspect);
        Assert.Equal("Photo by Ada", dto.MediaAttribution);
    }

    [Fact]
    public void A_malformed_topic_does_not_invent_an_aspect()
    {
        var loaded = VotingQuestionForm.From(new VotingQuestionDto(
            "q", "en", "m-music", "Prompt", "participants", [], "pending", "admin", null,
            "subject-only", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));

        Assert.Equal("subject-only", loaded.Subject);
        Assert.Null(loaded.Aspect);
    }
}
