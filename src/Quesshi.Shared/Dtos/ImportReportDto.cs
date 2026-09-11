namespace Quesshi.Shared;

/// <summary>
/// What a bulk import did, or would do. <see cref="DryRun"/> only echoes which mode the request ran
/// as — it makes no claim about how many rows landed, which <see cref="Accepted"/> already says, and
/// a dry run and a commit of the same file report the same <see cref="Rows"/> outcomes by design.
/// </summary>
public sealed record ImportReportDto(bool DryRun, int Total, int Accepted, int Rejected, List<ImportRowResultDto> Rows);

/// <summary>
/// One row's outcome. <see cref="Row"/> is a 1-based ordinal over data rows — CSV rows after the
/// header, or JSON array index + 1 — not a physical file line, since a quoted CSV field can embed a
/// newline. <see cref="Prompt"/> is null when the row was too structurally broken to read one.
/// </summary>
public sealed record ImportRowResultDto(int Row, string? Prompt, bool Accepted, string? Error);
