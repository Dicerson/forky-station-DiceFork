using Content.Server._Funkystation.SM.Components;
using Content.Server._Funkystation.SM.Events;
using Content.Server.Administration.Logs;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Shared._Funkystation.SM;
using Content.Shared.Atmos;
using Content.Shared.Database;
using Content.Shared.Mind.Components;
using Content.Shared.Singularity.Components;
using Content.Shared.Station.Components;
using Content.Shared.Tag;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Prototypes;

namespace Content.Server._Funkystation.SM.EntitySystems;

public sealed class SupermatterSystem : EntitySystem
{
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly AtmosphereSystem _atmosphereSystem = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly MapSystem _mapSystem = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly TagSystem _tagSystem = default!;
    [Dependency] private readonly TransformSystem _transformSystem = default!;

    private static readonly ProtoId<TagPrototype> HighRiskItemTag = "HighRiskItem";
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SupermatterComponent, AtmosDeviceUpdateEvent>(OnProcessSupermatter);
        SubscribeLocalEvent<MapGridComponent, SupermatterAttemptConsumeEntityEvent>(PreventConsume);
        SubscribeLocalEvent<StationDataComponent, SupermatterAttemptConsumeEntityEvent>(PreventConsume);
        SubscribeLocalEvent<SupermatterComponent, StartCollideEvent>(OnAshAbsorption);
        GasCharacteristicData.LoadFromPrototypes(_proto);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
    }

    /// <summary>
    /// Process logic for each supermatter.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    /// <param name="args"></param>
    private void OnProcessSupermatter(EntityUid uid, SupermatterComponent sm, AtmosDeviceUpdateEvent args)
    {
        AbsorbGas(uid, sm, args);
        ComputeGasCharacteristics(sm);
        ApplyStability(sm);
        ApplyEnthalpy(sm);
        ApplyGrowth(uid, sm);
        UpdateReproductionAndShards(uid, sm);
        sm.CurrentConductivity = sm.Conductivity;
        // …rest of the tick logic
    }

    /// <summary>
    /// Absorbs gas in a 3 x 3 area with the Supermatter at the center
    /// </summary>
    /// <param name="smUid"></param>
    /// <param name="sm"></param>
    /// <param name="args"></param>
    private void AbsorbGas(EntityUid smUid, SupermatterComponent sm, AtmosDeviceUpdateEvent args)
    {
        sm.CountVacuumTiles = 0;
        var ratio = 0.05f;
        if (args.Grid is not {} grid)
            return;
        var centerTile = _transformSystem.GetGridTilePositionOrDefault(smUid);

        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                var tile = centerTile + new Vector2i(dx, dy);

                var mixture = _atmosphereSystem.GetTileMixture(grid, args.Map, tile, excite: true);

                if (mixture == null)
                {
                    sm.CountVacuumTiles += 1;
                    continue;
                }

                if (mixture.Pressure < 10f)
                {
                    sm.CountVacuumTiles += 1;
                }

                var absorbed = mixture.RemoveRatio(ratio);
                for (var gas = 0; gas < Atmospherics.AdjustedNumberOfGases; gas++)
                {
                    var amount = absorbed.GetMoles(gas);
                    sm.AbsorbedGas.AdjustMoles(gas, amount);
                }
            }
        }
    }

    /// <summary>
    /// Computes the characteristics of the absorbed gas
    /// </summary>
    /// <param name="sm"></param>
    private void ComputeGasCharacteristics(SupermatterComponent sm)
    {
        float stability = 0f;
        float growth = 0f;
        float conductivity = 0f;
        float enthalpy = 0f;

        for (var i = 0; i < Atmospherics.AdjustedNumberOfGases; i++)
        {
            var moles = sm.AbsorbedGas.GetMoles(i);
            if (moles <= 0f)
                continue;

            var gas = (Gas)i;

            if (!GasCharacteristicData.GasTable.TryGetValue(gas, out var ch))
                continue;

            stability    += moles * ch.Stability;
            growth       += moles * ch.Growth;
            conductivity += moles * ch.Conductivity;
            enthalpy     += moles * ch.Enthalpy;
        }

        sm.Stability    = stability / 100f;
        sm.Growth       = growth / 100f;
        sm.Conductivity = conductivity / 100f;
        sm.Enthalpy     = enthalpy / 100f;

    }

    private void ApplyStability(SupermatterComponent sm)
    {
        var m = (10f - sm.Stability) / 10f;

        sm.Growth       *= m;
        sm.Conductivity *= m;
        sm.Enthalpy     *= m;

        sm.Power *= 1f - 0.08f * sm.Stability;
        sm.Power += sm.Stability;
    }

    private void ApplyEnthalpy( SupermatterComponent sm)
    {
        var deltaEnergy = sm.Enthalpy * 1_000_000f; // MJ → joules
        var temperature = sm.AbsorbedGas.Temperature;

        var sourceHeatCapacity = _atmosphereSystem.GetHeatCapacity(sm.AbsorbedGas, false);
        sm.AbsorbedGas.Temperature = _atmosphereSystem.GetThermalEnergy(sm.AbsorbedGas, sourceHeatCapacity + deltaEnergy);

        var deltaP = sm.Enthalpy * (temperature - 293.15f);
        sm.Power += deltaP;
    }

    private void ApplyGrowth(EntityUid uid, SupermatterComponent sm)
    {
        switch (sm.Growth)
        {
            //Negative Growth
            case < 0f:
            {
                var amount = -sm.Growth;
                var count = (int)MathF.Floor((sm.Power + 3000f) / 3000f);
                if (count < 1)
                    count = 1;

                var characteristics = new List<(float value, Gas gas)>
                {
                    (sm.Growth,        Gas.Ammonia),
                    (sm.Enthalpy >= 0 ? sm.Enthalpy : -sm.Enthalpy, sm.Enthalpy >= 0 ? Gas.Plasma     : Gas.Frezon),
                    (sm.Conductivity >= 0 ? sm.Conductivity : -sm.Conductivity, sm.Conductivity >= 0 ? Gas.WaterVapor : Gas.Oxygen),
                    (sm.Stability >= 0 ? sm.Stability : -sm.Stability, sm.Stability >= 0 ? Gas.Nitrogen   : Gas.Tritium),
                };
                characteristics.Sort((a, b) => MathF.Abs(b.value).CompareTo(MathF.Abs(a.value)));
                for (var i = 0; i < count && i < characteristics.Count; i++)
                {
                    var (_, gas) = characteristics[i];
                    sm.AbsorbedGas.AdjustMoles((int)gas, amount);
                }

                sm.Power -= amount * count;
                return;
            }
            //Positive growth
            case > 0f:
            {
                var fraction = sm.Growth / 45f;
                if (fraction <= 0f)
                    break;

                if (fraction > 1f)
                    fraction = 1f;

                var absorbed = sm.AbsorbedGas.RemoveRatio(fraction);
                _atmosphereSystem.Merge(sm.AbsorbedGas, absorbed);

                var absorbedMoles = 0f;
                for (var i = 0; i < Atmospherics.AdjustedNumberOfGases; i++)
                {
                    absorbedMoles += absorbed.GetMoles(i);
                }

                if (absorbedMoles <= 0f)
                    break;

                sm.Power += absorbedMoles;
                sm.Reproduction += absorbedMoles;
                break;
            }
        }

    }

    private void UpdateReproductionAndShards(EntityUid uid, SupermatterComponent sm)
    {
        sm.Reproduction *= 0.9f;

        sm.ReproductionProgress += sm.Reproduction;

        while (sm.ReproductionProgress >= 1000f)
        {
            sm.ReproductionProgress -= 1000f;

            var coords = Transform(uid).Coordinates;
            Spawn("SupermatterShard", coords);
        }
    }

    private void UpdateIntegrity(EntityUid uid, ref SupermatterComponent sm)
    {
        float delta = 0f;

        delta += sm.Stability;

        delta -= sm.Power / 500f;

        delta -= sm.CountVacuumTiles * sm.VacuumDamagePerTile;

        var gasTemp = sm.AbsorbedGas.Temperature;
        const float roomTemp = 293.15f;

        var tempDelta = ((gasTemp - roomTemp) / 100f) * sm.Enthalpy;
        delta += tempDelta;

        if (sm.AbsorptionHealingPool > 10f)
        {
            delta += sm.AbsorptionHealing;
            sm.AbsorptionHealingPool -= 10f;
        }

        var unclamped = delta;
        var clamped = Math.Clamp(delta, -2f, 2f);

        if (unclamped > 2f)
            clamped = 2f + (unclamped - delta);

        sm.Integrity += clamped;

        sm.Integrity = Math.Clamp(sm.Integrity, 0f, sm.MaxIntegrity);
    }

    public void OnAshAbsorption(EntityUid uid, SupermatterComponent sm, ref StartCollideEvent args)
    {
        AttemptConsumeEntity(uid, args.OtherEntity, sm);
    }
    public bool CanConsumeEntity(EntityUid hungry, EntityUid uid, SupermatterComponent sm)
    {
        var ev = new SupermatterAttemptConsumeEntityEvent(uid, hungry, sm);
        RaiseLocalEvent(uid, ref ev);
        return !ev.Cancelled;
    }

    public void ConsumeEntity(EntityUid hungry, EntityUid morsel, SupermatterComponent sm, BaseContainer? outerContainer = null)
    {
        if (EntityManager.IsQueuedForDeletion(morsel)) // already handled, and we're substepping
            return;

        if (HasComp<MindContainerComponent>(morsel)
            || _tagSystem.HasTag(morsel, HighRiskItemTag)
            || HasComp<ContainmentFieldGeneratorComponent>(morsel))
        {
            _adminLogger.Add(LogType.EntityDelete, LogImpact.High, $"{ToPrettyString(morsel):player} entered the Supermatter of {ToPrettyString(hungry)} and was deleted");
        }


        QueueDel(morsel);
        var evSelf = new EntityConsumedBySupermatterEvent(morsel, hungry, sm, outerContainer);
        var evEaten = new SupermatterConsumedEntityEvent(morsel, hungry, sm, outerContainer);
        RaiseLocalEvent(hungry, ref evSelf);
        RaiseLocalEvent(morsel, ref evEaten);
    }

    public bool AttemptConsumeEntity(EntityUid hungry, EntityUid morsel, SupermatterComponent sm, BaseContainer? outerContainer = null)
    {
        if (!CanConsumeEntity(hungry, morsel, sm))
            return false;

        ConsumeEntity(hungry, morsel, sm, outerContainer);
        return true;
    }

    public static void PreventConsume<TComp>(EntityUid uid, TComp comp, ref SupermatterAttemptConsumeEntityEvent args)
    {
        if (!args.Cancelled)
            args.Cancelled = true;
    }





}
