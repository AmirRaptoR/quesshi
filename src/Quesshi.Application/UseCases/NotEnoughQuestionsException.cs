namespace Quesshi.Application.UseCases;

public sealed class NotEnoughQuestionsException(string message) : Exception(message);
