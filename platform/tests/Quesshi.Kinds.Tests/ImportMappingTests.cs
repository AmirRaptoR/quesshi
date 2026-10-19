using Quesshi.Kinds;
using Quesshi.Kinds.Choice;
using Quesshi.Kinds.MapPick;
using Quesshi.Kinds.Order;
using Quesshi.Kinds.PollChoice;
using Quesshi.Kinds.PollRoster;
using Quesshi.Shared;

namespace Quesshi.Kinds.Tests;

public sealed class ImportMappingTests
{
    private static readonly ValidateEnv Env = new("en");

    [Fact]
    public void choice_mapping_exposes_its_legacy_csv_and_json_fields()
    {
        IImports mapping = new ChoiceKind();

        Assert.True(Exposes(mapping, ["choice1", "choice2", "choice3", "choice4", "correctIndex"]));
    }

    [Fact]
    public void order_mapping_exposes_its_legacy_csv_and_json_fields()
    {
        IImports mapping = new OrderKind();

        Assert.True(Exposes(mapping, ["item1", "item2", "item3", "item4"]));
    }

    [Fact]
    public void map_pick_mapping_exposes_its_legacy_csv_and_json_fields()
    {
        IImports mapping = new MapPickKind();

        Assert.True(Exposes(mapping, ["targetShape", "countryCode", "latitude", "longitude", "radiusKm", "baseLayer"]));
    }

    [Fact]
    public void poll_choice_mapping_exposes_its_legacy_csv_and_json_fields()
    {
        IImports mapping = new PollChoiceKind();

        Assert.True(Exposes(mapping, ["answerSource", "choice1", "choice2", "choice3", "choice4", "choice5", "choice6", "choice7", "choice8"]));
    }

    [Fact]
    public void poll_roster_mapping_exposes_its_legacy_csv_and_json_fields()
    {
        IImports mapping = new PollRosterKind();

        Assert.True(Exposes(mapping, ["answerSource", "choice1", "choice2", "choice3", "choice4", "choice5", "choice6", "choice7", "choice8"]));
    }

    [Fact]
    public void choice_mapping_selects_a_choice_row_case_insensitively()
    {
        var kind = new ChoiceKind();

        Assert.True(kind.Matches("CHOICE", Row()));
    }

    [Fact]
    public void order_mapping_selects_a_sort_row_case_insensitively()
    {
        var kind = new OrderKind();

        Assert.True(kind.Matches("SORT", Row()));
    }

    [Fact]
    public void map_pick_mapping_selects_a_map_row_case_insensitively()
    {
        var kind = new MapPickKind();

        Assert.True(kind.Matches("MAP", Row()));
    }

    [Fact]
    public void poll_choice_mapping_selects_a_fixed_answer_source_case_insensitively()
    {
        var kind = new PollChoiceKind();

        Assert.True(kind.Matches(null, Row(("ANSWERSOURCE", "FIXED"))));
    }

    [Fact]
    public void poll_roster_mapping_selects_a_participants_answer_source_case_insensitively()
    {
        var kind = new PollRosterKind();

        Assert.True(kind.Matches(null, Row(("answerSource", "PARTICIPANTS"))));
    }

    [Fact]
    public void choice_csv_fields_preserve_four_options_and_zero_based_correct_index()
    {
        var kind = new ChoiceKind();

        var payload = kind.Convert(Row(("CHOICE1", "Mercury"), ("choice2", "Venus"), ("choice3", "Earth"), ("choice4", "Mars"), ("CORRECTINDEX", "2")));

        Assert.Collection(payload.Options!,
            option => Assert.Equal("Mercury", option),
            option => Assert.Equal("Venus", option),
            option => Assert.Equal("Earth", option),
            option => Assert.Equal("Mars", option));
    }

    [Fact]
    public void choice_csv_fields_preserve_a_zero_based_correct_index()
    {
        var kind = new ChoiceKind();

        var payload = kind.Convert(Row(("choice1", "Mercury"), ("choice2", "Venus"), ("choice3", "Earth"), ("choice4", "Mars"), ("correctIndex", "2")));

        Assert.Equal(2, payload.CorrectIndex);
    }

    [Fact]
    public void order_json_fields_preserve_the_authored_correct_order()
    {
        var kind = new OrderKind();

        var payload = kind.Convert(Row(("item1", "First"), ("ITEM2", "Second"), ("item3", "Third"), ("item4", "Fourth")));

        Assert.Equal(["First", "Second", "Third", "Fourth"], payload.Items!.Select(item => item.Text));
    }

    [Fact]
    public void map_csv_fields_preserve_a_country_target_and_base_layer()
    {
        var kind = new MapPickKind();

        var payload = kind.Convert(Row(("targetShape", "country"), ("COUNTRYCODE", "NL"), ("baseLayer", "borders")));

        Assert.Equal(new MapPickPayload(MapBaseLayer.Borders, new MapTarget(MapTargetKind.Country, "NL")), payload);
    }

    [Fact]
    public void map_json_fields_preserve_a_city_target_and_base_layer()
    {
        var kind = new MapPickKind();

        var payload = kind.Convert(Row(("targetShape", "city"), ("latitude", "52.37"), ("LONGITUDE", "4.90"), ("radiusKm", "25"), ("baseLayer", "blank")));

        Assert.Equal(new MapPickPayload(MapBaseLayer.Blank, new MapTarget(MapTargetKind.Point, null, 52.37, 4.90, 25)), payload);
    }

    [Fact]
    public void map_mapping_ignores_irrelevant_fields_for_a_country_target()
    {
        var kind = new MapPickKind();

        var payload = kind.Convert(Row(("targetShape", "country"), ("countryCode", "NL"), ("latitude", "52.37"), ("longitude", "4.90"), ("radiusKm", "25"), ("baseLayer", "borders")));

        Assert.Empty(kind.Validate(payload, Env));
    }

    [Fact]
    public void fixed_choice_poll_fields_preserve_fixed_option_semantics()
    {
        var kind = new PollChoiceKind();

        var payload = kind.Convert(Row(("answerSource", "fixed"), ("choice1", "Yes"), ("CHOICE2", "No")));

        Assert.Equal(["Yes", "No"], payload.Options!.Select(option => option!.Text));
    }

    [Fact]
    public void participant_roster_poll_fields_preserve_roster_semantics()
    {
        var kind = new PollRosterKind();

        var payload = kind.Convert(Row(("answerSource", "participants")));

        Assert.Empty(kind.Validate(payload, Env));
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("json")]
    public void incomplete_choice_rows_produce_validation_problems(string source)
    {
        var kind = new ChoiceKind();
        var payload = kind.Convert(source == "csv"
            ? Row(("choice1", "A"), ("choice2", "B"), ("choice3", "C"), ("choice4", "D"), ("correctIndex", "not-a-number"))
            : Row(("CHOICE1", "A"), ("choice2", "B"), ("choice3", "C"), ("choice4", "D"), ("CORRECTINDEX", "4")));

        Assert.NotEmpty(kind.Validate(payload, Env));
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("json")]
    public void incomplete_order_rows_produce_validation_problems(string source)
    {
        var kind = new OrderKind();
        var payload = kind.Convert(source == "csv"
            ? Row(("item1", "One"), ("item2", "Two"), ("item3", "Three"))
            : Row(("ITEM1", "One"), ("item2", "Two"), ("item3", "Three"), ("item4", "")));

        Assert.NotEmpty(kind.Validate(payload, Env));
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("json")]
    public void incomplete_map_rows_produce_validation_problems(string source)
    {
        var kind = new MapPickKind();
        var payload = kind.Convert(source == "csv"
            ? Row(("targetShape", "country"), ("countryCode", "zz"), ("baseLayer", "borders"))
            : Row(("TARGETSHAPE", "city"), ("latitude", "not-a-number"), ("longitude", "4.90"), ("radiusKm", "25"), ("baseLayer", "blank")));

        Assert.NotEmpty(kind.Validate(payload, Env));
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("json")]
    public void incomplete_fixed_poll_rows_produce_validation_problems(string source)
    {
        var kind = new PollChoiceKind();
        var payload = kind.Convert(source == "csv"
            ? Row(("answerSource", "fixed"), ("choice1", "Only one"))
            : Row(("ANSWERSOURCE", "fixed"), ("CHOICE1", "Only one")));

        Assert.NotEmpty(kind.Validate(payload, Env));
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("json")]
    public void fixed_options_on_a_participant_poll_produce_validation_problems(string source)
    {
        var kind = new PollRosterKind();
        var payload = kind.Convert(source == "csv"
            ? Row(("answerSource", "participants"), ("choice1", "Not allowed"))
            : Row(("ANSWERSOURCE", "participants"), ("CHOICE1", "Not allowed")));

        Assert.NotEmpty(kind.Validate(payload, Env));
    }

    [Fact]
    public void choice_csv_example_round_trips_to_a_valid_payload()
    {
        var kind = new ChoiceKind();

        Assert.Empty(kind.Validate(kind.Convert(new ImportRow(kind.CsvExample.Fields)), Env));
    }

    [Fact]
    public void choice_json_example_round_trips_to_a_valid_payload()
    {
        var kind = new ChoiceKind();

        Assert.Empty(kind.Validate(kind.Convert(new ImportRow(kind.JsonExample.Fields)), Env));
    }

    [Fact]
    public void order_csv_and_json_examples_round_trip_to_valid_payloads()
    {
        var kind = new OrderKind();

        Assert.Empty(kind.Validate(kind.Convert(new ImportRow(kind.CsvExample.Fields)), Env).Concat(kind.Validate(kind.Convert(new ImportRow(kind.JsonExample.Fields)), Env)));
    }

    [Fact]
    public void map_pick_csv_and_json_examples_round_trip_to_valid_payloads()
    {
        var kind = new MapPickKind();

        Assert.Empty(kind.Validate(kind.Convert(new ImportRow(kind.CsvExample.Fields)), Env).Concat(kind.Validate(kind.Convert(new ImportRow(kind.JsonExample.Fields)), Env)));
    }

    [Fact]
    public void poll_choice_csv_and_json_examples_round_trip_to_valid_payloads()
    {
        var kind = new PollChoiceKind();

        Assert.Empty(kind.Validate(kind.Convert(new ImportRow(kind.CsvExample.Fields)), Env).Concat(kind.Validate(kind.Convert(new ImportRow(kind.JsonExample.Fields)), Env)));
    }

    [Fact]
    public void poll_roster_csv_and_json_examples_round_trip_to_valid_payloads()
    {
        var kind = new PollRosterKind();

        Assert.Empty(kind.Validate(kind.Convert(new ImportRow(kind.CsvExample.Fields)), Env).Concat(kind.Validate(kind.Convert(new ImportRow(kind.JsonExample.Fields)), Env)));
    }

    private static ImportRow Row(params (string Name, string? Value)[] fields) => new(fields.ToDictionary(field => field.Name, field => field.Value));

    private static bool Exposes(IImports mapping, IReadOnlyList<string> fields) =>
        mapping.CsvColumns.SequenceEqual(fields) && mapping.JsonFields.SequenceEqual(fields);
}
