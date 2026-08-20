using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public class QuestionTopUpTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly InMemoryQuestions _questions = new();
    private readonly InMemoryCategories _categories = new();
    private readonly InMemoryGenerationLog _log = new();
    private readonly FakeClock _clock = FakeClock.At2026();
    private readonly SeqIds _ids = new();

    private static GeneratedQuestion Good(string prompt) => new(prompt, ["a", "b", "c", "d"], 1, null);

    private static GeneratedQuestion About(string prompt, string subject, string aspect)
        => new(prompt, ["a", "b", "c", "d"], 1, null) { Subject = subject, Aspect = aspect };

    private TopUpQuestionBank Sut(IQuestionGenerator gen, int target = 2)
        => new(_questions, _categories, gen, _log, _clock, _ids, new TopUpOptions { TargetPerBucket = target, MaxPerRun = 100 });

    private void OneCategory() => _categories.UpsertAsync(new Category("geography", "geography-fa", "Geography", "*", "#fff"));

    [Fact]
    public async Task Generates_for_buckets_that_are_below_target()
    {
        OneCategory();
        var gen = new ScriptedGenerator(Good("p1"), Good("p2"));
        var run = await Sut(gen).RunAsync();

        Assert.True(run.Inserted > 0);
        Assert.All(_questions.Items, q => Assert.Equal(QuestionStatus.Approved, q.Status));
        Assert.All(_questions.Items, q => Assert.Equal(QuestionSource.Ai, q.Source));
    }

    [Fact]
    public async Task A_category_is_only_topped_up_in_languages_it_already_has()
    {
        OneCategory();

        // Stocked in Dutch only — as KNM is. The other two languages must be left alone rather than
        // triggering a whole new bank of paid calls.
        foreach (var level in MatchRules.AllLevels)
            await _questions.UpsertAsync(Question.Create($"nl-{level}", Language.Nl, "geography", level,
                $"vraag {level}", ["a", "b", "c", "d"], 0, T0, status: QuestionStatus.Approved));

        var gen = new ScriptedGenerator(Good("p1"), Good("p2"));
        await Sut(gen, target: 20).RunAsync();

        Assert.NotEmpty(gen.Languages);
        Assert.All(gen.Languages, lang => Assert.Equal(Language.Nl, lang));
    }

    [Fact]
    public async Task A_brand_new_category_is_still_filled_in_every_language()
    {
        OneCategory();
        var gen = new ScriptedGenerator(Good("p1"), Good("p2"));
        await Sut(gen, target: 20).RunAsync();

        Assert.Contains(Language.Fa, gen.Languages);
        Assert.Contains(Language.En, gen.Languages);
        Assert.Contains(Language.Nl, gen.Languages);
    }

    [Fact]
    public async Task Leaves_healthy_buckets_alone()
    {
        OneCategory();
        foreach (var lang in new[] { Language.Fa, Language.En, Language.Nl })
            foreach (var level in MatchRules.AllLevels)
                for (var i = 0; i < 5; i++)
                    await _questions.UpsertAsync(Question.Create($"{lang}{level}{i}", lang, "geography", level,
                        $"stocked {lang}{level}{i}", ["a", "b", "c", "d"], 0, T0, status: QuestionStatus.Approved));

        var gen = new ScriptedGenerator(Good("new"));
        var run = await Sut(gen).RunAsync();

        Assert.Equal(0, gen.Calls);
        Assert.Equal(0, run.Inserted);
    }

    [Fact]
    public async Task Malformed_generated_questions_are_rejected_not_stored()
    {
        OneCategory();
        var gen = new ScriptedGenerator(
            new GeneratedQuestion("only two", ["a", "b"], 0, null),
            new GeneratedQuestion("out of range", ["a", "b", "c", "d"], 9, null),
            new GeneratedQuestion("   ", ["a", "b", "c", "d"], 0, null),
            new GeneratedQuestion("duplicate choices", ["a", "a", "c", "d"], 0, null),
            Good("the only good one"));

        var run = await Sut(gen).RunAsync();

        Assert.All(_questions.Items, q => Assert.Equal("the only good one", q.Prompt));
        Assert.True(run.Rejected >= 4);
    }

    [Fact]
    public async Task A_prompt_that_already_exists_is_not_inserted_twice()
    {
        OneCategory();
        await _questions.UpsertAsync(Question.Create("existing", Language.En, "geography", Difficulty.Easy,
            "Capital of Peru?", ["a", "b", "c", "d"], 0, T0, status: QuestionStatus.Approved));

        var gen = new ScriptedGenerator(Good("capital of peru?"));
        await Sut(gen).RunAsync();

        Assert.Single(_questions.Items.Where(q => q.Prompt.Equals("Capital of Peru?", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task A_reworded_version_of_an_existing_question_is_not_inserted()
    {
        OneCategory();
        await _questions.UpsertAsync(Question.Create("existing", Language.En, "geography", Difficulty.Easy,
            "What is the capital of Peru?", ["a", "b", "c", "d"], 0, T0, status: QuestionStatus.Approved));

        var gen = new ScriptedGenerator(Good("Which city is the capital of Peru?"), Good("Which river runs through Cairo?"));
        await Sut(gen).RunAsync();

        Assert.DoesNotContain(_questions.Items, q => q.Prompt.Contains("capital of Peru", StringComparison.OrdinalIgnoreCase) && q.Id != "existing");
        Assert.Contains(_questions.Items, q => q.Prompt.Contains("Cairo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task One_batch_cannot_repeat_itself()
    {
        OneCategory();
        var gen = new ScriptedGenerator(
            Good("Which country has the largest land area?"),
            Good("What country has the largest land area?"),
            Good("Which ocean lies between Africa and Australia?"));

        await Sut(gen).RunAsync();

        var landArea = _questions.Items.Count(q => q.Prompt.Contains("largest land area", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, landArea);
    }

    [Fact]
    public async Task Generating_one_bucket_on_demand_only_touches_that_bucket()
    {
        OneCategory();
        var gen = new ScriptedGenerator(Good("Which sea has no coastline?"));

        var run = await Sut(gen).GenerateOnceAsync(Language.En, "geography", Difficulty.Hard, 1);

        Assert.Equal(1, run.Inserted);
        Assert.All(_questions.Items, q =>
        {
            Assert.Equal(Language.En, q.Lang);
            Assert.Equal(Difficulty.Hard, q.Level);
            Assert.Equal(QuestionStatus.Approved, q.Status);
        });
    }

    [Fact]
    public async Task Generating_for_a_category_that_does_not_exist_fails_loudly_in_the_log()
    {
        var run = await Sut(new ScriptedGenerator(Good("x"))).GenerateOnceAsync(Language.En, "nope", Difficulty.Easy, 3);

        Assert.Equal(0, run.Inserted);
        Assert.NotNull(run.Error);
    }

    [Fact]
    public async Task An_unconfigured_generator_is_a_no_op_not_a_crash()
    {
        OneCategory();
        var gen = new ScriptedGenerator(Good("p")) { IsConfigured = false };
        var run = await Sut(gen).RunAsync();

        Assert.Equal(0, gen.Calls);
        Assert.Equal(0, run.Inserted);
        Assert.Empty(_questions.Items);
    }

    [Fact]
    public async Task Review_first_is_still_available_by_configuration()
    {
        OneCategory();
        var sut = new TopUpQuestionBank(_questions, _categories, new ScriptedGenerator(Good("p1")), _log, _clock, _ids,
            new TopUpOptions { TargetPerBucket = 2, MaxPerRun = 100, AutoApprove = false });

        await sut.RunAsync();

        Assert.NotEmpty(_questions.Items);
        Assert.All(_questions.Items, q => Assert.Equal(QuestionStatus.Pending, q.Status));
    }

    [Fact]
    public async Task Every_run_is_written_to_the_log()
    {
        OneCategory();
        await Sut(new ScriptedGenerator(Good("p1"))).RunAsync();
        Assert.Single(_log.Runs);
        Assert.NotNull(_log.Runs[0].FinishedAt);
    }

    [Fact]
    public async Task Inactive_categories_are_not_topped_up()
    {
        _categories.UpsertAsync(new Category("dead", "x", "dead", "*", "#fff", IsActive: false));
        var gen = new ScriptedGenerator(Good("p"));
        await Sut(gen).RunAsync();
        Assert.Equal(0, gen.Calls);
    }

    [Fact]
    public async Task Two_questions_about_the_same_thing_are_stored_once()
    {
        OneCategory();

        // Same subject and aspect, no content words in common — the wording rule cannot see this.
        var gen = new ScriptedGenerator(
            About("Wie regisseerde Inception?", "Inception", "director"),
            About("Door welke filmmaker werd Inception gemaakt?", "inception", "Director"));

        await Sut(gen, target: 1).RunAsync();

        Assert.Single(_questions.Items.Where(q => q.Topic == "inception|director"));
    }

    [Fact]
    public async Task The_same_topic_may_exist_once_per_language()
    {
        OneCategory();
        await _questions.UpsertAsync(Question.Create("existing-nl", Language.Nl, "geography", Difficulty.Easy,
            "Wie regisseerde Inception?", ["a", "b", "c", "d"], 0, T0,
            status: QuestionStatus.Approved, topic: "inception|director"));

        var gen = new ScriptedGenerator(About("Who directed Inception?", "Inception", "director"));
        var run = await Sut(gen, target: 1).GenerateOnceAsync(Language.En, "geography", Difficulty.Easy, 1);

        // The Dutch one does not block the English one; only a repeat inside a language does.
        Assert.Equal(1, run.Inserted);
        Assert.Equal(2, _questions.Items.Count(q => q.Topic == "inception|director"));
    }

    [Fact]
    public void A_topic_key_is_null_unless_both_halves_are_present()
    {
        Assert.Equal("inception|director", TopicKey.From(" Inception ", "Director"));
        Assert.Null(TopicKey.From("Inception", ""));
        Assert.Null(TopicKey.From(null, "director"));
    }
}
