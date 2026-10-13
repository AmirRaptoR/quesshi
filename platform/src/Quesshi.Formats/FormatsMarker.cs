using Quesshi.Engine.Contracts;

namespace Quesshi.Formats;

public sealed class FormatsMarker;

/// <summary>The built-in platform formats explicitly available to a host.</summary>
public static class PlatformFormats
{
    public static IFormatRegistry CreateRegistry() => new FormatRegistry([new Poll.PollFormat()]);
}
