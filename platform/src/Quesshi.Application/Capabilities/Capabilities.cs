namespace Quesshi.Application;

internal interface ICapabilities
{
    Task<bool> IsOnAsync(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default);

    Task<TSettings> GetSettingsAsync<TSettings>(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default)
        where TSettings : notnull;

    Task SetAsync<TSettings>(Guid tenantId, CapabilityType type, string key, bool enabled, TSettings settings, CancellationToken cancellationToken = default)
        where TSettings : notnull;
}

internal sealed class CapabilityValidationException(IReadOnlyList<string> messages) : Exception(string.Join(Environment.NewLine, messages))
{
    public IReadOnlyList<string> Messages { get; } = messages;
}
