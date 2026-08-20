namespace Quesshi.Application.UseCases;

public enum PasswordChangeStatus { Ok, WrongCurrentPassword, WeakPassword, InvalidToken, ExpiredToken, UsedToken, NoSuchAccount }
