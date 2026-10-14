using ChoiceComponents = Quesshi.Web.Plugins.Choice;
using OrderComponents = Quesshi.Web.Plugins.Order;
using PollChoiceComponents = Quesshi.Web.Plugins.PollChoice;
using PollRosterComponents = Quesshi.Web.Plugins.PollRoster;

namespace Quesshi.Web.Plugins;

/// <summary>Registers the client components compiled into the Web shell.</summary>
public sealed class WebPluginsEntry : IKindClientAssemblyEntry
{
    public void Register(IClientRegistry registry)
    {
        registry.Register(new TestKindClient());
        registry.Register(new ChoiceKindClient());
        registry.Register(new OrderKindClient());
        registry.Register(new PollChoiceKindClient());
        registry.Register(new PollRosterKindClient());
    }
}

public sealed class TestKindClient : IKindClient
{
    public string Id => "test";
    public Type? Play(int version) => version switch
    {
        1 => typeof(TestKindPlayV1),
        2 => typeof(TestKindPlayV2),
        _ => null
    };

    public Type? Reveal(int version) => version switch
    {
        1 => typeof(TestKindRevealV1),
        2 => typeof(TestKindRevealV2),
        _ => null
    };

    public Type Editor => typeof(TestKindEditor);
}

public sealed class ChoiceKindClient : IKindClient
{
    public string Id => "choice";
    public Type? Play(int version) => version == 1 ? typeof(ChoiceComponents.ChoicePlay) : null;
    public Type? Reveal(int version) => version == 1 ? typeof(ChoiceComponents.ChoiceReveal) : null;
    public Type Editor => typeof(ChoiceComponents.ChoiceEditor);
}

public sealed class OrderKindClient : IKindClient
{
    public string Id => "order";
    public Type? Play(int version) => version == 1 ? typeof(OrderComponents.OrderPlay) : null;
    public Type? Reveal(int version) => version == 1 ? typeof(OrderComponents.OrderReveal) : null;
    public Type Editor => typeof(OrderComponents.OrderEditor);
}

public sealed class PollChoiceKindClient : IKindClient
{
    public string Id => "poll_choice";
    public Type? Play(int version) => version == 1 ? typeof(PollChoiceComponents.PollChoicePlay) : null;
    public Type? Reveal(int version) => version == 1 ? typeof(PollChoiceComponents.PollChoiceReveal) : null;
    public Type Editor => typeof(PollChoiceComponents.PollChoiceEditor);
}

public sealed class PollRosterKindClient : IKindClient
{
    public string Id => "poll_roster";
    public Type? Play(int version) => version == 1 ? typeof(PollRosterComponents.PollRosterPlay) : null;
    public Type? Reveal(int version) => version == 1 ? typeof(PollRosterComponents.PollRosterReveal) : null;
    public Type Editor => typeof(PollRosterComponents.PollRosterEditor);
}
