using Quesshi.Domain;

namespace Quesshi.Application.UseCases;

public sealed record AdminLoginResult(AdminLoginStatus Status, AdminUser? User, DateTimeOffset? LockedUntil = null);
