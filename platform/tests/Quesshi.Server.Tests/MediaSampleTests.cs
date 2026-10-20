using Quesshi.Application.Media;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class MediaSampleTests
{
    public static IEnumerable<object[]> Samples => MediaSamples.All.Select(sample => new object[] { sample });

    [Theory]
    [MemberData(nameof(Samples))]
    public void Each_checked_in_sample_has_its_manifest_kind_and_mime(MediaSample sample)
    {
        var accepted = MediaSniffer.TryInspect(MediaSamples.ReadBytes(sample), out var kind, out var mime, out _);

        Assert.Equal((true, sample.Kind, sample.Mime), (accepted, kind, mime));
    }
}
