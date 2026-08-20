using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>How much stock one (language, category, level) bucket holds.</summary>
public sealed record BucketCount(Language Lang, string CategoryId, Difficulty Level, int Approved, int Pending);
