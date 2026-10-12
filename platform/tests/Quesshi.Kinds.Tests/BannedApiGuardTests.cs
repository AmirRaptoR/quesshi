using System.Diagnostics;

namespace Quesshi.Kinds.Tests;

public sealed class BannedApiGuardTests
{
    [Fact]
    public async Task UtcNow_and_NewGuid_are_compile_errors()
    {
        var exitCode = await BannedApiCompileProbe.RunAsync("Quesshi.Kinds");

        Assert.NotEqual(0, exitCode);
    }
}

public static class BannedApiCompileProbe
{
    public static async Task<int> RunAsync(string projectName)
    {
        var platform = FindPlatformDirectory();
        var probe = Path.Combine(Path.GetTempPath(), $"quesshi-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(probe, "public sealed class Probe { public System.DateTime Clock() => System.DateTime.UtcNow; public System.Guid Id() => System.Guid.NewGuid(); }");

        try
        {
            using var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = platform,
                UseShellExecute = false,
                ArgumentList = { "build", $"src/{projectName}/{projectName}.csproj", "--no-restore", $"-p:QuesshiBannedApiProbe={probe}" }
            })!;
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        finally
        {
            File.Delete(probe);
        }
    }

    private static string FindPlatformDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Quesshi.Platform.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the platform solution.");
    }
}
