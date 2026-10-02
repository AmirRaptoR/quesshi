using Quesshi.Application.Ports;

namespace Quesshi.Server.Tests;

public sealed class FakeContentSettingsRepository(ICategoryRepository categories) : IContentSettingsRepository
{
    public ContentSettings? Value { get; set; }
    public async Task<ContentSettings> GetAsync(CancellationToken ct = default)
    {
        if (Value is not null) return Value;
        var ids = (await categories.AllAsync(ct)).Select(x => x.Id).ToList();
        return new ContentSettings(ids, ids);
    }
    public Task<ContentSettings> GetOrCreateAsync(ContentSettings initial, CancellationToken ct = default)
    {
        Value ??= initial;
        return Task.FromResult(Value);
    }
    public Task SaveAsync(ContentSettings settings, CancellationToken ct = default)
    {
        Value = settings;
        return Task.CompletedTask;
    }
}
