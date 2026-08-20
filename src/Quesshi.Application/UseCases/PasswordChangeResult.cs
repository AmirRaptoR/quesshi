namespace Quesshi.Application.UseCases;

public sealed record PasswordChangeResult(PasswordChangeStatus Status, IReadOnlyList<string> Problems)
{
    public static readonly PasswordChangeResult Ok = new(PasswordChangeStatus.Ok, []);

    public static PasswordChangeResult Fail(PasswordChangeStatus status) => new(status, []);

    public static PasswordChangeResult Weak(IReadOnlyList<string> problems) => new(PasswordChangeStatus.WeakPassword, problems);
}
