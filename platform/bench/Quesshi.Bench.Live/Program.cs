using System.Text.Json;

namespace Quesshi.Bench.Live;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = BenchmarkOptions.Parse(args);
            var report = await new LiveBenchmarkRunner().RunAsync(options);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (options.Output is { Length: > 0 }) await File.WriteAllTextAsync(options.Output, json);
            Console.WriteLine(json);
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"Benchmark failed: {exception.Message}");
            return 1;
        }
    }
}
