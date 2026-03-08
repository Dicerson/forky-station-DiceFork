using Content.Server._Funkystation.SM.EntitySystems;
using Content.Shared._Funkystation.SM.Components;
using Content.Shared.Atmos;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Server._Funkystation.SM.Components;

[RegisterComponent]
[Access(typeof(SupermatterSystem))]
public sealed partial class SupermatterComponent : SharedSupermatterComponent
{
    // --- Core State ---
    [DataField("power")]
    public float Power = 0f;
    [DataField("integrity")]
    public float Integrity = 1000f;

    // --- Gas Characteristics (calculated each tick) ---
    [DataField("stability")]
    public float Stability = 10f;
    [DataField("conductivity")]
    public float Conductivity = 0f;
    [DataField("enthalpy")]
    public float Enthalpy = 0f;
    [DataField("growth")]
    public float Growth = 0f;

    // --- Internal Buffers ---
    [DataField("absorbedGas")]
    public GasMixture AbsorbedGas = new();
    [DataField("reproduction")]
    public float Reproduction = 0f;
    [DataField("absorptionHealingPool")]
    public float AbsorptionHealingPool = 0f;

    // --- Lightning ---
    [DataField("lightningTimer")]
    public float LightningTimer = 0f;

    // --- Cached Values for Visuals ---
    [DataField("lastTemperature")]
    public float LastTemperature = 293.15f;
    [DataField("lastMaxCharacteristic")]
    public float LastMaxCharacteristic = 0f;
    [DataField("visualState")]
    public SupermatterState VisualState = SupermatterState.Inactive;
}
