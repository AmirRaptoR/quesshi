using System.Reflection;
using Microsoft.AspNetCore.Components.WebAssembly.Services;
using Quesshi.Web.Plugins;

namespace Quesshi.Web;

public sealed class LazyAssemblyClientLoader(LazyAssemblyLoader loader) : IClientAssemblyLoader
{
    public async Task<IReadOnlyList<Assembly>> LoadAsync(string assemblyName) =>
        (await loader.LoadAssembliesAsync([assemblyName])).ToArray();
}
