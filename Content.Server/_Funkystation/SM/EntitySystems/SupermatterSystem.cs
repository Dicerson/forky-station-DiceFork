using System.Linq;
using Content.Server._Funkystation.SM.Components;
using Content.Server._Funkystation.SM.Events;
using Content.Server.Administration.Logs;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Shared._Funkystation.SM.Components;
using Content.Shared._Funkystation.SM.Prototypes;
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
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        LoadGasCharacteristics();
    }

    /// <summary>
    /// checks if the GasCharacteristicsPrototype was modified
    /// </summary>
    /// <param name="ev"></param>
    private void OnPrototypesReloaded(PrototypesReloadedEventArgs ev)
    {
        if (ev.WasModified<GasCharacteristicsPrototype>())
            LoadGasCharacteristics();
    }

    /// <summary>
    /// Loads the Gas characteristics from yml
    /// </summary>
    private void LoadGasCharacteristics()
    {
        var newTable = new Dictionary<Gas, GasCharacteristics>();

        foreach (var proto in _proto.EnumeratePrototypes<GasCharacteristicsPrototype>())
        {
            if (!Enum.TryParse<Gas>(proto.ID, out var gas))
                continue;

            newTable[gas] = new GasCharacteristics(
                proto.Stability,
                proto.Growth,
                proto.Conductivity,
                proto.Enthalpy
            );
        }

        foreach (var sm in EntityQuery<SupermatterComponent>())
        {
            sm.GasTable = newTable;
        }
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
        UpdateIntegrity(uid, sm);
        // …rest of the tick logic
    }

    /// <summary>
    /// Helper function that is a list of offsets
    /// </summary>
    private static readonly Vector2i[] AbsorptionOffsets =
    {
        new(-1, -1), new(0, -1), new(1, -1),
        new(-1,  0), new(0,  0), new(1,  0),
        new(-1,  1), new(0,  1), new(1,  1)
    };

    /// <summary>
    /// Absorbs gas in a 3 x 3 area with the Supermatter at the center
    /// </summary>
    /// <param name="smUid"></param>
    /// <param name="sm"></param>
    /// <param name="args"></param>
    private void AbsorbGas(EntityUid smUid, SupermatterComponent sm, AtmosDeviceUpdateEvent args)
    {
        sm.CountVacuumTiles = 0;
        var ratio = sm.RatioPerTile;
        if (args.Grid is not {} grid)
            return;
        var centerTile = _transformSystem.GetGridTilePositionOrDefault(smUid);

        foreach (var offset in AbsorptionOffsets)
        {
            var tile = centerTile + offset;

            var mixture = _atmosphereSystem.GetTileMixture(grid, args.Map, tile, excite: true);

            if (mixture == null)
            {
                sm.CountVacuumTiles++;
                continue;
            }

            var pressure = mixture.Pressure;
            if (pressure < sm.VacuumThreshold)
                sm.CountVacuumTiles++;

            if(pressure <= 0)
                continue;

            var absorbed = mixture.RemoveRatio(ratio);

            foreach (var (gas, moles) in absorbed)
            {
                sm.AbsorbedGas.AdjustMoles(gas, moles);
            }
        }
    }

    /// <summary>
    /// Computes the characteristics of the absorbed gas
    /// </summary>
    /// <param name="sm"></param>
    private void ComputeGasCharacteristics(SupermatterComponent sm)
    {
        float stability = sm.BaseStability;
        float growth = sm.BaseGrowth;
        float conductivity = sm.BaseConductivity;
        float enthalpy = sm.BaseEnthalpy;

        foreach (var (gas, moles) in sm.AbsorbedGas )
        {
            if (moles <= 0f)
                continue;

            if (!sm.GasTable.TryGetValue(gas, out var ch))
                continue;

            stability    += moles * ch.Stability;
            growth       += moles * ch.Growth;
            conductivity += moles * ch.Conductivity;
            enthalpy     += moles * ch.Enthalpy;
        }

        // conversion to percentage
        sm.Stability    = stability / 100f;
        sm.Growth       = growth / 100f;
        sm.Conductivity = conductivity / 100f;
        sm.Enthalpy     = enthalpy / 100f;

    }

    /// <summary>
    /// Updates the stability
    /// </summary>
    /// <param name="sm"></param>
    private void ApplyStability(SupermatterComponent sm)
    {
        var m = (sm.NeutralStability - sm.Stability) / sm.NeutralStability;

        sm.Growth       *= m;
        sm.Conductivity *= m;
        sm.Enthalpy     *= m;

        sm.Power *= 1f - sm.StabilityPowerDrainScale * sm.Stability;
        sm.Power += sm.Stability;
    }

    /// <summary>
    /// Updates the Enthalpy
    /// </summary>
    /// <param name="sm"></param>
    private void ApplyEnthalpy( SupermatterComponent sm)
    {
        var deltaEnergy = sm.Enthalpy * 1_000_000f; // MJ → joules
        var temperature = sm.AbsorbedGas.Temperature;

        var sourceHeatCapacity = _atmosphereSystem.GetHeatCapacity(sm.AbsorbedGas, false);
        sm.AbsorbedGas.Temperature = _atmosphereSystem.GetThermalEnergy(sm.AbsorbedGas, sourceHeatCapacity + deltaEnergy);

        var deltaP = sm.Enthalpy * (temperature - 293.15f); // temperature - room temperature in Kelvin
        sm.Power += deltaP;
    }

    /// <summary>
    /// Updates the growth
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    private void ApplyGrowth(EntityUid uid, SupermatterComponent sm)
    {
        switch (sm.Growth)
        {
            //Negative Growth
            case < 0f:
            {
                var amount = -sm.Growth;
                var count = (int)MathF.Floor((sm.Power + sm.PowerPerGasPacket) / sm.PowerPerGasPacket);
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
                var fraction = sm.Growth / sm.GrowthAbsorptionScale;
                if (fraction <= 0f)
                    break;

                if (fraction > 1f)
                    fraction = 1f;

                var absorbed = sm.AbsorbedGas.RemoveRatio(fraction);
                _atmosphereSystem.Merge(sm.AbsorbedGas, absorbed);

                var absorbedMoles = absorbed.TotalMoles;

                if (absorbedMoles <= 0f)
                    break;

                sm.Power += absorbedMoles;
                sm.Reproduction += absorbedMoles;
                break;
            }
        }

    }

    /// <summary>
    /// Updates the repoduction and creates a shard when reaching the threshold
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    private void UpdateReproductionAndShards(EntityUid uid, SupermatterComponent sm)
    {
        sm.Reproduction *= sm.ReproductionDecay;

        sm.ReproductionProgress += sm.Reproduction;

        while (sm.ReproductionProgress >= sm.ReproductionThreshold)
        {
            sm.ReproductionProgress -= sm.ReproductionThreshold;

            var coords = Transform(uid).Coordinates;
            Spawn("SupermatterShard", coords);
        }
    }

    private void UpdateIntegrity(EntityUid uid, SupermatterComponent sm)
    {
        var delta = 0f;

        delta += sm.Stability;

        delta -= sm.Power / sm.PowerDamageScale;

        delta -= sm.CountVacuumTiles * sm.VacuumDamagePerTile;

        var gasTemp = sm.AbsorbedGas.Temperature;
        const float roomTemp = 293.15f;

        var tempDelta = ((gasTemp - roomTemp) / sm.TemperatureDamageScale) * sm.Enthalpy;
        delta += tempDelta;

        if (sm.AbsorptionHealingPool > sm.AbsorptionHealingCost)
        {
            delta += sm.AbsorptionHealing;
            sm.AbsorptionHealingPool -= sm.AbsorptionHealingCost;
        }

        var unclamped = delta;
        var cap = sm.IntegrityChangeCap;

        var clamped = Math.Clamp(delta, -cap, cap);

        if (unclamped > cap)
            clamped = cap + (unclamped - delta);

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
