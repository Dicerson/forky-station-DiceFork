using Content.Server._Funkystation.SM.EntitySystems;
using Content.Shared._Funkystation.SM.Components;
using Content.Shared.Atmos;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Server._Funkystation.SM.Components;

[RegisterComponent, NetworkedComponent]
[Access(typeof(SupermatterSystem))]
public sealed partial class SupermatterComponent : SharedSupermatterComponent
{
    // --- Core State ---
    [DataField("power")]
    public float Power;
    [DataField("integrity")]
    public float Integrity = 1000f;
    [DataField("maxIntegrity")]
    public float MaxIntegrity = 1000f;
    [DataField("vacuumDamagePerTile")]
    public float VacuumDamagePerTile = 0.5f;
    [DataField("absorptionHealing")]
    public float AbsorptionHealing = 1f;


    // --- Gas Characteristics (calculated each tick) ---
    [DataField("stability")]
    public float Stability = 10f;
    [DataField("conductivity")]
    public float Conductivity;
    [DataField("currentConductivity")]
    public float CurrentConductivity;
    [DataField("enthalpy")]
    public float Enthalpy;
    [DataField("growth")]
    public float Growth;

    // --- Internal Buffers ---
    [DataField("absorbedGas")]
    public GasMixture AbsorbedGas;
    [DataField("reproduction")]
    public float Reproduction;
    [DataField("reproductionProgress")]
    public float ReproductionProgress;
    [DataField("absorptionHealingPool")]
    public float AbsorptionHealingPool;
    [DataField("countVacuumTiles")]
    public int CountVacuumTiles;

    // --- Lightning ---
    [DataField("lightningTimer")]
    public float LightningTimer;

    // --- Cached Values for Visuals ---
    [DataField("lastTemperature")]
    public float LastTemperature = 293.15f;
    [DataField("lastMaxCharacteristic")]
    public float LastMaxCharacteristic;
    [DataField("visualState")]
    public SupermatterState VisualState = SupermatterState.Inactive;
}
