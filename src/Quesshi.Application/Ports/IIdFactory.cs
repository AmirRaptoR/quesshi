namespace Quesshi.Application.Ports;

public interface IIdFactory
{
    string NewId();

    /// <summary>Short, human-shareable challenge code (no ambiguous characters).</summary>
    string NewMatchCode();
}
