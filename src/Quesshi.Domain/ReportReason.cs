namespace Quesshi.Domain;

/// <summary>Why a player thinks a question is bad. Kept short: a long list makes people pick at random.</summary>
public enum ReportReason
{
    /// <summary>The marked answer is not right.</summary>
    WrongAnswer = 0,

    /// <summary>More than one answer works, or the wording is ambiguous.</summary>
    Unclear = 1,

    /// <summary>Already asked, in this or another question.</summary>
    Duplicate = 2,

    Offensive = 3,
    Other = 4
}
