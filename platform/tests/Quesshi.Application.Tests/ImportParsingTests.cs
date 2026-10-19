using System.Text;
using Quesshi.Application.Imports;

namespace Quesshi.Application.Tests;

public sealed class ImportParsingTests
{
    [Fact]
    public async Task Csv_with_a_bom_quoted_newline_and_extra_columns_preserves_the_row()
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("\ufefflanguage,prompt,choice1,extra\r\nen,\"First line\nsecond line\",one,ignored\r\n"));

        var (error, document) = await ImportParsing.ParseAsync("csv", input, input.Length, ["language", "prompt", "choice1"]);

        Assert.Null(error);
        Assert.Equal("First line\nsecond line", document!.Rows.Single().Fields["prompt"]);
    }

    [Fact]
    public async Task Json_non_object_elements_are_reported_as_bad_rows_without_rejecting_later_rows()
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("[{\"prompt\":\"first\"},42,{\"prompt\":\"last\"}]"));

        var (error, document) = await ImportParsing.ParseAsync("json", input, input.Length, []);

        Assert.Null(error);
        Assert.Collection(document!.Rows,
            row => Assert.False(row.IsMalformed),
            row => Assert.True(row.IsMalformed),
            row => Assert.Equal("last", row.Fields["prompt"]));
    }

    [Fact]
    public async Task Csv_missing_a_required_header_is_a_request_error()
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("language,prompt\r\nen,question\r\n"));

        var (error, _) = await ImportParsing.ParseAsync("csv", input, input.Length, ["language", "choice1"]);

        Assert.Equal("missing_header", error);
    }
}
