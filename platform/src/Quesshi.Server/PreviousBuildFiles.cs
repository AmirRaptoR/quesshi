using Microsoft.Extensions.FileProviders;

namespace Quesshi.Server;

/// <summary>
/// Serves the previous build's fingerprinted client files beside the current ones, so a tab left open
/// across a release can still load the lazy assemblies and runtime files its old shell refers to.
/// </summary>
public static class PreviousBuildFiles
{
    /// <summary>Directory under the application base that holds the previous build's published wwwroot.</summary>
    public const string DirectoryName = "wwwroot-previous";

    /// <summary>Adds the previous build's <c>_framework</c> and <c>_content</c> files that the current build lacks.</summary>
    public static IApplicationBuilder UsePreviousBuildFiles(this IApplicationBuilder app, string currentRoot, string previousRoot)
    {
        if (!Directory.Exists(previousRoot)) return app;
        return app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PreviousOnlyFileProvider(new PhysicalFileProvider(currentRoot), new PhysicalFileProvider(previousRoot))
        });
    }

    // Anything the current build also has (the shell, the stylesheet) is answered by the current build,
    // never by an older copy, so only files that no longer exist there fall back to the previous build.
    private sealed class PreviousOnlyFileProvider(IFileProvider current, IFileProvider previous) : IFileProvider
    {
        public IFileInfo GetFileInfo(string subpath)
        {
            var path = subpath.TrimStart('/');
            if (!path.StartsWith("_framework/", StringComparison.Ordinal) && !path.StartsWith("_content/", StringComparison.Ordinal))
                return new NotFoundFileInfo(subpath);
            return current.GetFileInfo(subpath).Exists ? new NotFoundFileInfo(subpath) : previous.GetFileInfo(subpath);
        }

        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

        public Microsoft.Extensions.Primitives.IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    }
}
