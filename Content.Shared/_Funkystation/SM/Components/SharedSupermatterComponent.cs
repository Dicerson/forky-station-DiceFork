

using Robust.Shared.Serialization;

namespace Content.Shared._Funkystation.SM.Components;

[Virtual]
public abstract partial class SharedSupermatterComponent : Component
{

}
[Serializable, NetSerializable]
public enum SupermatterState : byte
{
    Inactive,
    Stable,
    Unstable,
    Critical,
    Delaminating
}
