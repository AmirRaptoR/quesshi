namespace Quesshi.Coverage;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "merge" => Merge(args[1..]),
                "check" => Check(args[1..]),
                "baseline" => Baseline(args[1..]),
                "select-baseline" => SelectBaseline(args[1..]),
                _ => Usage()
            };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or System.Xml.XmlException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static int Merge(string[] args)
    {
        var output = Value(args, "--output");
        var directories = Values(args, "--input-directory");
        CoberturaMerger.Merge(directories, output);
        return 0;
    }

    private static int Check(string[] args)
    {
        var result = ChangedLineCoverageGate.Evaluate(Value(args, "--coverage"), Value(args, "--diff"), Value(args, "--repository-root"));
        if (result.Passed)
        {
            return 0;
        }

        Console.Error.Write(result.FailureOutput());
        return 1;
    }

    private static int Baseline(string[] args)
    {
        var result = BaselineCoverageGate.Evaluate(Value(args, "--coverage"), OptionalValue(args, "--baseline"), Value(args, "--repository-root"));
        if (result.Passed)
        {
            return 0;
        }

        Console.Error.Write(result.FailureOutput());
        return 1;
    }

    private static int SelectBaseline(string[] args)
    {
        var runId = BaselineRunSelector.SelectNewestSuccessfulMainRun(Value(args, "--workflow-runs"), Value(args, "--artifacts"));
        if (runId is not null)
        {
            Console.Write(runId);
        }

        return 0;
    }

    private static string Value(IReadOnlyList<string> args, string option)
    {
        var index = Array.IndexOf(args.ToArray(), option);
        if (index < 0 || index == args.Count - 1)
        {
            throw new ArgumentException($"Missing {option}.");
        }

        return args[index + 1];
    }

    private static IReadOnlyList<string> Values(IReadOnlyList<string> args, string option)
    {
        var values = Enumerable.Range(0, args.Count - 1).Where(index => args[index] == option).Select(index => args[index + 1]).ToArray();
        if (values.Length == 0)
        {
            throw new ArgumentException($"Missing {option}.");
        }

        return values;
    }

    private static string? OptionalValue(IReadOnlyList<string> args, string option)
    {
        var index = Array.IndexOf(args.ToArray(), option);
        if (index < 0)
        {
            return null;
        }

        if (index == args.Count - 1)
        {
            throw new ArgumentException($"Missing {option}.");
        }

        return args[index + 1];
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: coverage merge --output <path> --input-directory <path> [...] | coverage check --coverage <path> --diff <path> --repository-root <path> | coverage baseline --coverage <path> [--baseline <path>] --repository-root <path> | coverage select-baseline --workflow-runs <path> --artifacts <path>");
        return 2;
    }
}
