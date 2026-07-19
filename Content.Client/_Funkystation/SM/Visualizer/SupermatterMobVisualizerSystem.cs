using Content.Shared._Funkystation.SM.Components;
using Content.Shared._Funkystation.SM.Visuals;
using Robust.Client.GameObjects;
using Robust.Shared.Utility;

namespace Content.Client._Funkystation.SM.Visualizer;
/// <summary>
/// Controls client-side visuals for getting ashed.
/// </summary>
public sealed class SupermatterMobVisualizerSystem: VisualizerSystem<SupermatterMobVisualsComponent>
{
    protected override void OnAppearanceChange(EntityUid uid, SupermatterMobVisualsComponent comp, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        if (!AppearanceSystem.TryGetData<bool>(uid, SupermatterVisuals.Cloaked, out var cloaked, args.Component))
            return;

        if (cloaked)
        {
            SpriteSystem.LayerSetRsi((uid, args.Sprite), 0, new ResPath("/Textures/_RMC14/Effects/cloak.rsi"), "cloak");
            SpriteSystem.LayerSetRsiState((uid, args.Sprite), 0, "cloak");
        }
    }
}
