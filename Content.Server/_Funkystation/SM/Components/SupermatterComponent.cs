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
    public float Power;
    [DataField("integrity")]
    public float Integrity = 1000f;
    [DataField("maxIntegrity")]
    public float MaxIntegrity = 1000f;
    [DataField("vacuumDamagePerTile")]
    public float VacuumDamagePerTile = 0.5f;
    [DataField("absorptionHealing")]
    public float AbsorptionHealing = 1f;
    [DataField("ratioPerTile")]
    public float RatioPerTile = 0.09f;
    [DataField("vacuumThreshold")]
    public float VacuumThreshold = 10f;


    // --- Process values ---
    [DataField("reproductionThreshold")]
    public float ReproductionThreshold = 1000f;
    [DataField("reproductionDecay")]
    public float ReproductionDecay = 0.9f;
    [DataField("powerDamageScale")]
    public float PowerDamageScale = 500f;
    [DataField("temperatureDamageScale")]
    public float TemperatureDamageScale = 100f;
    [DataField("absorptionHealingCost")]
    public float AbsorptionHealingCost = 10f;
    [DataField("growthAbsorptionScale")]
    public float GrowthAbsorptionScale = 45f;
    [DataField("powerPerGasPacket")]
    public float PowerPerGasPacket = 3000f;
    [DataField("neutralStability")]
    public float NeutralStability = 10f;
    [DataField("stabilityPowerDrainScale")]
    public float StabilityPowerDrainScale = 0.08f;
    [DataField("baseStability")]
    public float BaseStability = 10f;
    [DataField("baseGrowth")]
    public float BaseGrowth;
    [DataField("baseConductivity")]
    public float BaseConductivity;
    [DataField("baseEnthalpy")]
    public float BaseEnthalpy;
    [DataField("integrityChangeCap")]
    public float IntegrityChangeCap = 2f;

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
    [DataField("powerPool")]
    public float PowerPool;
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

    /// <summary>
    /// Whether the entity this supermatter is attached to is being absorbed by another supermatter.
    /// </summary>
    [ViewVariables(VVAccess.ReadOnly)]
    public bool BeingAbsorbedByAnotherSupermatter = false;
}
