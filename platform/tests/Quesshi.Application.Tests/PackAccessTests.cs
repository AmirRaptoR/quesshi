using Quesshi.Application.Packs;

namespace Quesshi.Application.Tests;

public sealed class PackAccessTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static Pack PackWith(PackVisibility visibility, Guid? owner) =>
        new(Guid.NewGuid(), owner, "Pack", null, "en", visibility, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Owner_uses_and_edits_a_private_pack_and_another_player_does_neither()
    {
        var pack = PackWith(PackVisibility.Private, Owner);

        Assert.True(PackAccess.CanUse(pack, new PackActor(Owner)));
        Assert.True(PackAccess.CanEdit(pack, new PackActor(Owner)));
        Assert.False(PackAccess.CanUse(pack, new PackActor(Other)));
        Assert.False(PackAccess.CanEdit(pack, new PackActor(Other)));
        Assert.False(PackAccess.CanUse(pack, new PackActor(null)));
    }

    [Fact]
    public void Anyone_uses_an_unlisted_pack_but_only_the_owner_edits_it()
    {
        var pack = PackWith(PackVisibility.Unlisted, Owner);

        Assert.True(PackAccess.CanUse(pack, new PackActor(Other)));
        Assert.True(PackAccess.CanUse(pack, new PackActor(null)));
        Assert.False(PackAccess.CanEdit(pack, new PackActor(Other)));
        Assert.False(PackAccess.CanEdit(pack, new PackActor(null)));
        Assert.True(PackAccess.CanEdit(pack, new PackActor(Owner)));
    }

    [Fact]
    public void Any_player_of_the_tenant_uses_a_tenant_pack_but_only_the_owner_edits_it()
    {
        var pack = PackWith(PackVisibility.Tenant, Owner);

        Assert.True(PackAccess.CanUse(pack, new PackActor(Other)));
        Assert.False(PackAccess.CanUse(pack, new PackActor(null)));
        Assert.False(PackAccess.CanEdit(pack, new PackActor(Other)));
        Assert.True(PackAccess.CanEdit(pack, new PackActor(Owner)));
        Assert.False(PackAccess.CanEdit(pack, new PackActor(Other, IsTenantAdmin: true)));
    }

    [Fact]
    public void A_tenant_admin_edits_a_tenant_owned_pack_and_a_plain_player_does_not()
    {
        var pack = PackWith(PackVisibility.Private, null);

        Assert.True(PackAccess.CanEdit(pack, new PackActor(null, IsTenantAdmin: true)));
        Assert.True(PackAccess.CanEdit(pack, new PackActor(Other, IsTenantAdmin: true)));
        Assert.False(PackAccess.CanEdit(pack, new PackActor(Other)));
        Assert.False(PackAccess.CanEdit(pack, new PackActor(null)));
        Assert.False(PackAccess.CanUse(pack, new PackActor(Other)));
    }
}
