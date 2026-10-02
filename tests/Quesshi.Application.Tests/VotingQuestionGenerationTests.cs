using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class VotingQuestionGenerationTests
{
    private readonly InMemoryVotingQuestions _questions = new();
    private readonly InMemoryVotingCategories _categories = new();
    private readonly InMemoryVotingGenerationLog _log = new();
    private readonly FakeClock _clock = FakeClock.At2026();
    private readonly SeqIds _ids = new();

    public VotingQuestionGenerationTests()
        => _categories.Items.Add(new VotingCategory("m-friends", "دوستان", "Friends", "👥", "#123456"));

    private GenerateVotingQuestions Sut(ScriptedVotingGenerator generator,
        bool autoApprove = false, int maxBatch = 20)
        => new(_questions, _categories, generator, _log, _clock, _ids,
            new VotingGenerationOptions { AutoApprove = autoApprove, MaxBatchSize = maxBatch });

    [Fact]
    public async Task Participant_generation_pins_voting_shape_provenance_and_review_status()
    {
        var generator = new ScriptedVotingGenerator(
            new GeneratedVotingQuestion("Who would plan the best surprise?", [],
                "planning a surprise", "initiative"));

        var run = await Sut(generator).RunAsync(Language.En, "m-friends",
            VotingAnswerSource.Participants, 4);

        Assert.Equal(1, run.Inserted);
        Assert.Equal(0, run.Rejected);
        var question = Assert.Single(_questions.Items);
        Assert.Equal(VotingAnswerSource.Participants, question.AnswerSource);
        Assert.Empty(question.FixedChoices);
        Assert.Equal(QuestionSource.Ai, question.Source);
        Assert.Equal(QuestionStatus.Pending, question.Status);
        Assert.Equal("planning a surprise|initiative", question.Topic);
        Assert.Equal(run, Assert.Single(_log.Runs));
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task Fixed_generation_validates_choices_and_can_be_explicitly_auto_approved()
    {
        var generator = new ScriptedVotingGenerator(
            new GeneratedVotingQuestion("Where should we go next?", ["Beach", "Mountains", "City"],
                "next trip", "preference"),
            new GeneratedVotingQuestion("Bad participant-shaped row", [], "bad row", "shape"));

        var run = await Sut(generator, autoApprove: true).RunAsync(Language.En, "m-friends",
            VotingAnswerSource.Fixed, 2);

        Assert.Equal(1, run.Inserted);
        Assert.Equal(1, run.Rejected);
        var question = Assert.Single(_questions.Items);
        Assert.Equal(["Beach", "Mountains", "City"], question.FixedChoices);
        Assert.Equal(QuestionStatus.Approved, question.Status);
    }

    [Fact]
    public async Task Generated_rows_require_identity_and_deduplicate_topics_and_paraphrased_prompts()
    {
        await _questions.UpsertAsync(VotingQuestion.Create("existing", Language.En, "m-friends",
            "Who usually picks the restaurant?", VotingAnswerSource.Participants, null, _clock.Now,
            source: QuestionSource.Admin, topic: "dinner choice|initiative"));
        var generator = new ScriptedVotingGenerator(
            new GeneratedVotingQuestion("Who chooses where everyone eats?", [], "dinner choice", "initiative"),
            new GeneratedVotingQuestion("Who usually picks the restaurant for us?", [], "restaurants", "preference"),
            new GeneratedVotingQuestion("Who brings the best snacks?", [], "", "generosity"));

        var run = await Sut(generator).RunAsync(Language.En, "m-friends",
            VotingAnswerSource.Participants, 3);

        Assert.Equal(0, run.Inserted);
        Assert.Equal(3, run.Rejected);
        Assert.Single(_questions.Items);
        Assert.Contains("Who usually picks the restaurant?", generator.LastAvoid);
    }

    [Theory]
    [InlineData("ارزش‌ها و تصمیم‌های خانوادگی", "تصمیم خانوادگی 011")]
    [InlineData("family decision 11", "preference")]
    [InlineData("choosing dinner", "choosing dinner")]
    public async Task Generated_rows_reject_translated_numbered_or_repeated_identity_parts(
        string subject, string aspect)
    {
        var generator = new ScriptedVotingGenerator(
            new GeneratedVotingQuestion("Who would choose dinner?", [], subject, aspect));

        var run = await Sut(generator).RunAsync(Language.Fa, "m-friends",
            VotingAnswerSource.Participants, 1);

        Assert.Equal(0, run.Inserted);
        Assert.Equal(1, run.Rejected);
        Assert.Empty(_questions.Items);
    }

    [Fact]
    public async Task Missing_inactive_and_unconfigured_inputs_finish_a_logged_noop()
    {
        var unconfigured = new ScriptedVotingGenerator { IsConfigured = false };
        var noKey = await Sut(unconfigured).RunAsync(Language.En, "m-friends",
            VotingAnswerSource.Participants, 5);
        Assert.Equal("generator_not_configured", noKey.Error);
        Assert.Equal(0, unconfigured.Calls);

        var configured = new ScriptedVotingGenerator();
        var missing = await Sut(configured).RunAsync(Language.En, "m-missing",
            VotingAnswerSource.Participants, 5);
        Assert.Equal("unknown_category", missing.Error);

        _categories.Items.Add(new VotingCategory("m-retired", "", "Retired", "◆", "#000", false));
        var inactive = await Sut(configured).RunAsync(Language.En, "m-retired",
            VotingAnswerSource.Participants, 5);
        Assert.Equal("inactive_category", inactive.Error);
        Assert.Equal(3, _log.Runs.Count);
        Assert.Equal(0, configured.Calls);
    }

    [Fact]
    public async Task Request_count_is_bounded_by_voting_generation_configuration()
    {
        var generator = new ScriptedVotingGenerator();

        var run = await Sut(generator, maxBatch: 3).RunAsync(Language.Nl, "m-friends",
            VotingAnswerSource.Fixed, 20);

        Assert.Equal(3, run.Requested);
        Assert.Equal(3, generator.LastCount);
        Assert.Equal(Language.Nl, generator.LastLanguage);
        Assert.Equal(VotingAnswerSource.Fixed, generator.LastAnswerSource);
    }
}
