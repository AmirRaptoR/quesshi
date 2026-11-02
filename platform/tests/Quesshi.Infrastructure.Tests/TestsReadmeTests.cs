namespace Quesshi.Infrastructure.Tests;

public sealed class TestsReadmeTests
{
    [Fact]
    public void Readme_documents_the_stale_database_sweeps()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.Name != "tests")
            dir = dir.Parent;
        var readme = Path.Combine(dir!.FullName, "README.md");
        var text = string.Join(' ', File.ReadAllText(readme).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains("PostgresFixture", text);
        Assert.Contains("`quesshi_test_*` databases created more than 6 hours ago", text);
        Assert.Contains("plain `DROP DATABASE`", text);
        Assert.Contains("any drop failure is ignored", text);
        Assert.Contains("`LiveBenchmarkRunner` does the same for `quesshi_bench_*`", text);
    }
}
