using System.Collections.Generic;

namespace Quesshi.Kinds.First
{
    public sealed class DependsOnSibling
    {
        public Quesshi.Kinds.Second.Sibling? Sibling { get; init; }
    }

    public sealed class UsesSiblingOnlyInMethod
    {
        public object Create() => new Quesshi.Kinds.Second.Sibling();
    }

    public sealed class UsesSiblingInsideGenericSignature
    {
        public List<Quesshi.Kinds.Second.Sibling> Values { get; } = [];
    }
}

namespace Quesshi.Kinds.Second
{
    public sealed class Sibling;
}
