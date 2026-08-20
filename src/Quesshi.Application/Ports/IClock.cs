namespace Quesshi.Application.Ports;

public interface IClock
{
    DateTimeOffset Now { get; }
}
