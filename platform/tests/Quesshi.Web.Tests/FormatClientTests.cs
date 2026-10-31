using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Web.Plugins;

namespace Quesshi.Web.Tests;

public sealed class FormatClientTests : BunitContext
{
    [Fact]
    public void Resolves_settings_screen_and_result_of_a_registered_format()
    {
        var registry = new ClientRegistry(new NoLoader());
        registry.RegisterFormat(new FakeFormat("fake"));

        var client = registry.ResolveFormat("fake")!;

        Assert.Same(typeof(FirstTile), client.Settings);
        Assert.Same(typeof(SecondTile), client.Screen);
        Assert.Same(typeof(ThrowingTile), client.Result);
        Assert.Null(registry.ResolveFormat("other"));
    }

    [Fact]
    public void A_format_not_offered_for_new_play_still_resolves_for_an_existing_session()
    {
        var registry = new ClientRegistry(new NoLoader());
        registry.RegisterFormat(new FakeFormat("retired"));
        var offered = new HomeTileParameters(["live"]);

        Assert.DoesNotContain("retired", offered.EnabledFormats);
        Assert.NotNull(registry.ResolveFormat("retired"));
    }

    [Fact]
    public void A_slot_gives_a_contribution_only_its_declared_parameter_type()
    {
        var registry = new ClientRegistry(new NoLoader());
        registry.RegisterSlot(ClientSlots.ProfileSection, typeof(ProfileTile));
        Assert.Throws<ArgumentException>(() => registry.RegisterSlot(ClientSlots.HomeTile, typeof(ProfileTile)));
        Services.AddSingleton<IFormatClientRegistry>(registry);

        var cut = Render<ClientSlotHost<ProfileSectionParameters>>(p => p
            .Add(c => c.Slot, ClientSlots.ProfileSection)
            .Add(c => c.Value, new ProfileSectionParameters("p1")));

        Assert.Equal("profile:p1", cut.Find("span").TextContent);
        Assert.Empty(registry.Contributions(ClientSlots.HomeTile));
    }

    [Fact]
    public void Contributions_render_in_registry_order()
    {
        var registry = SlotRegistry(typeof(FirstTile), typeof(SecondTile));

        var cut = RenderHomeSlot(registry);

        Assert.Equal(["first", "second"], cut.FindAll("span").Select(e => e.TextContent));
    }

    [Fact]
    public void A_throwing_contribution_leaves_a_gap_and_its_siblings_usable()
    {
        var registry = SlotRegistry(typeof(FirstTile), typeof(ThrowingTile), typeof(SecondTile));

        var cut = RenderHomeSlot(registry);

        Assert.Equal(["first", "second"], cut.FindAll("span").Select(e => e.TextContent));
        Assert.Single(cut.FindAll("[data-slot-gap='home.tile']"));
    }

    [Fact]
    public void Two_pages_with_the_same_path_fail_the_collision_check()
    {
        var registry = new ClientRegistry(new NoLoader());
        registry.RegisterPage("/ranks", typeof(FirstTile));

        Assert.Throws<InvalidOperationException>(() => registry.RegisterPage("ranks/", typeof(SecondTile)));
        registry.RegisterPage("/other", typeof(SecondTile));
        Assert.Equal(["/ranks", "/other"], registry.Pages.Select(p => p.Path));
    }

    private ClientRegistry SlotRegistry(params Type[] components)
    {
        var registry = new ClientRegistry(new NoLoader());
        foreach (var component in components) registry.RegisterSlot(ClientSlots.HomeTile, component);
        return registry;
    }

    private IRenderedComponent<ClientSlotHost<HomeTileParameters>> RenderHomeSlot(ClientRegistry registry)
    {
        Services.AddSingleton<IFormatClientRegistry>(registry);
        return Render<ClientSlotHost<HomeTileParameters>>(p => p
            .Add(c => c.Slot, ClientSlots.HomeTile)
            .Add(c => c.Value, new HomeTileParameters(["live"])));
    }

    private sealed class NoLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => throw new NotSupportedException();
    }

    private sealed class FakeFormat(string id) : IFormatClient
    {
        public string Id => id;
        public Type Settings => typeof(FirstTile);
        public Type Screen => typeof(SecondTile);
        public Type Result => typeof(ThrowingTile);
    }

    private sealed class FirstTile : ComponentBase
    {
        [Parameter] public HomeTileParameters Parameters { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        { b.OpenElement(0, "span"); b.AddContent(1, "first"); b.CloseElement(); }
    }

    private sealed class SecondTile : ComponentBase
    {
        [Parameter] public HomeTileParameters Parameters { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        { b.OpenElement(0, "span"); b.AddContent(1, "second"); b.CloseElement(); }
    }

    private sealed class ThrowingTile : ComponentBase
    {
        [Parameter] public HomeTileParameters Parameters { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class ProfileTile : ComponentBase
    {
        [Parameter] public ProfileSectionParameters Parameters { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        { b.OpenElement(0, "span"); b.AddContent(1, "profile:" + Parameters.PlayerId); b.CloseElement(); }
    }
}
