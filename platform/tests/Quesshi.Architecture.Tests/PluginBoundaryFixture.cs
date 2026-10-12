namespace Quesshi.Kinds.First
{
    public sealed class DependsOnSibling
    {
        public Quesshi.Kinds.Second.Sibling? Sibling { get; init; }
    }
}

namespace Quesshi.Kinds.Second
{
    public sealed class Sibling;
}
