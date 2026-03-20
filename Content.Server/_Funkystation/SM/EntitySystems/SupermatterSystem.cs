using System.Linq;
using Content.Server._Funkystation.SM.Components;
using Content.Server._Funkystation.SM.Events;
using Content.Server.Administration.Logs;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Shared._Funkystation.Mobs;
using Content.Shared._Funkystation.SM.Components;
using Content.Shared._Funkystation.SM.Prototypes;
using Content.Shared.Atmos;
using Content.Shared.Damage.Systems;
using Content.Shared.Database;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Radiation.Components;
using Content.Shared.Station.Components;
using Content.Shared.Tag;
using Content.Shared.Tools.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Prototypes;

namespace Content.Server._Funkystation.SM.EntitySystems;

public sealed class SupermatterSystem : EntitySystem
{
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly AtmosphereSystem _atmosphereSystem = default!;
    [Dependency] private readonly SharedContainerSystem _containerSystem = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly TagSystem _tagSystem = default!;
    [Dependency] private readonly TransformSystem _transformSystem = default!;
    [Dependency] private readonly SharedTransformSystem _xformSystem = default!;

    private static readonly ProtoId<TagPrototype> HighRiskItemTag = "HighRiskItem";
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SupermatterComponent, AtmosDeviceUpdateEvent>(OnProcessSupermatter);
        SubscribeLocalEvent<MapGridComponent, SupermatterAttemptConsumeEntityEvent>(PreventConsume);
        SubscribeLocalEvent<StationDataComponent, SupermatterAttemptConsumeEntityEvent>(PreventConsume);
        SubscribeLocalEvent<SupermatterComponent, EntGotInsertedIntoContainerMessage>(OnSupermatterContained);
        SubscribeLocalEvent<SupermatterContainedEvent>(OnSupermatterContained);
        SubscribeLocalEvent<SupermatterComponent, SupermatterAttemptConsumeEntityEvent>(OnAnotherSupermatterAttemptAbsorbThisSupermatter);
        SubscribeLocalEvent<SupermatterComponent, SupermatterConsumedEntityEvent>(OnAnotherSupermatterAbsorbedThisSupermatter);
        SubscribeLocalEvent<SupermatterComponent, EntityAshedBySupermatterEvent>(OnAshed);
        SubscribeLocalEvent<SupermatterComponent, StartCollideEvent>(OnAshAbsorption);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        SubscribeLocalEvent<ContainerManagerComponent, SupermatterConsumedEntityEvent>(OnContainerConsumed);
        SubscribeLocalEvent<SupermatterComponent, DamageChangedEvent>(OnDamage);
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
        sm.AbsorbedGas.Clear();
        AbsorbGas(uid, sm, args);
        ApplyPowerPool(sm);
        ComputeGasCharacteristics(sm);
        ApplyPowerMultipliers(sm);
        ApplyStability(sm);
        ApplyEnthalpy(sm);
        ApplyGrowth(sm);
        UpdateReproductionAndShards(uid, sm);
        sm.CurrentConductivity = sm.Conductivity;
        UpdateIntegrity(sm);
        CheckDelamination(uid, sm);
        if (sm.Delaminated)
            return;

        ComputeRadiation(sm);
        EmitRadiation(uid, sm);

        ReleaseGas(uid, sm, args);
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
    /// Adds the power from when an entity is ashed to the SM
    /// </summary>
    /// <param name="sm"></param>
    private void ApplyPowerPool(SupermatterComponent sm)
    {
        if (sm.PowerPool <= 0f)
            return;

        var gained = sm.PowerPool * 0.10f; // 10%
        sm.Power += gained;
        sm.PowerPool -= gained;
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
        //sm.Stability    = stability / 100f;
        //sm.Growth       = growth / 100f;
        //sm.Conductivity = conductivity / 100f;
        //sm.Enthalpy     = enthalpy / 100f;

    }

    /// <summary>
    /// Characteristic Multiplication by Power
    /// </summary>
    /// <param name="sm"></param>
    private void ApplyPowerMultipliers(SupermatterComponent sm)
    {
        var multiplier = 1f + sm.Power / sm.PowerScalingFactor;
        sm.Growth *= multiplier;
        sm.Conductivity *= multiplier;
        sm.Enthalpy *= multiplier;
    }

    /// <summary>
    /// Updates the stability
    /// </summary>
    /// <param name="sm"></param>
    private void ApplyStability(SupermatterComponent sm)
    {
        var stabilityEffectScale = (sm.NeutralStability - sm.Stability) / sm.NeutralStability;

        sm.Growth       *= stabilityEffectScale;
        sm.Conductivity *= stabilityEffectScale;
        sm.Enthalpy     *= stabilityEffectScale;

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
        sm.Power = sm.Enthalpy * (sm.AbsorbedGas.Temperature - sm.RoomTemp); // temperature - room temperature in Kelvin
        _atmosphereSystem.AddHeat(sm.AbsorbedGas, deltaEnergy);
    }

    /// <summary>
    /// Updates the growth
    /// </summary>
    /// <param name="sm"></param>
    private void ApplyGrowth(SupermatterComponent sm)
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
    /// Updates the reproduction and creates a shard when reaching the threshold
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

    /// <summary>
    /// Updates the integrity of the supermatter crystal
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    private void UpdateIntegrity(SupermatterComponent sm)
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

    /// <summary>
    /// Checks if the supermatter is delaminating
    /// and raises an event if it is
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    private void CheckDelamination(EntityUid uid, SupermatterComponent sm)
    {
        if (sm.Integrity > 0)
            return;
        var dominant = GetDominantCharacteristic(sm);
        var ev = new SupermatterDelaminationEvent(uid, dominant);
        RaiseLocalEvent(uid, ref ev);
        sm.Delaminated = true;
    }

    /// <summary>
    /// Gets the dominant Characteristics for delamination
    /// </summary>
    /// <param name="sm"></param>
    /// <returns></returns>
    private GasCharacteristicsType GetDominantCharacteristic(SupermatterComponent sm)
    {
        // Start with Growth as the default
        var dominant = GasCharacteristicsType.Growth;
        var max = MathF.Abs(sm.Growth);

        var conductivity = MathF.Abs(sm.Conductivity);
        if (conductivity > max)
        {
            max = conductivity;
            dominant = GasCharacteristicsType.Conductivity;
        }

        var enthalpy = MathF.Abs(sm.Enthalpy);
        if (enthalpy > max)
        {
            max = enthalpy;
            dominant = GasCharacteristicsType.Enthalpy;
        }

        var stability = MathF.Abs(sm.Stability);
        if (stability > max)
        {
            dominant = GasCharacteristicsType.Stability;
        }

        return dominant;
    }

    /// <summary>
    /// Computes the radiation output of the Supermatter based on power and stability
    /// </summary>
    /// <param name="sm"></param>
    private void ComputeRadiation(SupermatterComponent sm)
    {
        var baseRadiation = sm.BaseRadiation + (sm.Power * sm.PowerPercentage);
        var stabilityMultiplier = (10f - sm.Stability) / 10f;
        sm.CurrentRadiation = baseRadiation * stabilityMultiplier;
    }

    /// <summary>
    /// Updates the RadiationSourceComponent with the current radiation intensity of the supermatter
    /// </summary>
    /// <param name="smUid"></param>
    /// <param name="sm"></param>
    private void EmitRadiation(EntityUid smUid, SupermatterComponent sm)
    {
        var rad = EnsureComp<RadiationSourceComponent>(smUid);
        rad.Intensity = sm.CurrentRadiation;
    }

    /// <summary>
    /// Releases the gases the sm absorbed and produced
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    /// <param name="args"></param>
    private void ReleaseGas(EntityUid uid,SupermatterComponent sm, AtmosDeviceUpdateEvent args)
    {
        if (args.Grid is not {} grid)
            return;
        var centerTile = _transformSystem.GetGridTilePositionOrDefault(uid);

        var mixture = _atmosphereSystem.GetTileMixture(grid, args.Map, centerTile, excite: true);
        if (mixture == null)
            return;

        _atmosphereSystem.Merge(mixture, sm.AbsorbedGas);
    }

    // This whole section could potentially be reduced by using the
    // Event horizon consumption system as most of the functions are taken from there
    // and changed a bit to fit the supermatter.
    // Credit to TemporalOroboros <TemporalOroboros@gmail.com> for the original functions.
    #region Ashing
    /// <summary>
    /// Handles supermatter ashing any entities they bump into.
    /// The supermatter will not ash any entities if it itself has been absorbed by a supermatter.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    /// <param name="args"></param>
    private void OnAshAbsorption(EntityUid uid, SupermatterComponent sm, ref StartCollideEvent args)
    {
        AttemptAshEntity(uid, args.OtherEntity, sm);
    }

    /// <summary>
    /// Attempts to ash all entities within a container.
    /// Excludes the supermatter itself.
    /// All immune entities within the container will be dumped to a given container or the map/grid if that is impossible.
    /// </summary>
    /// <param name="hungry"></param>
    /// <param name="container"></param>
    /// <param name="sm"></param>
    /// <param name="outerContainer"></param>
    private void AshEntitiesInContainer(EntityUid hungry, BaseContainer container, SupermatterComponent sm, BaseContainer? outerContainer = null)
    {
        // Removing the immune entities from the container needs to be deferred until after iteration or the iterator raises an error.
        List<EntityUid> immune = new();

        foreach (var entity in container.ContainedEntities)
        {
            if (entity == hungry || !AttemptAshEntity(hungry, entity, sm, outerContainer))
                immune.Add(entity); // The first check keeps supermatters an admin smited into a locker from ashing themselves.
                                    // The second check keeps things that have been rendered immune to supermatters from being deleted by a supermatter eating their container.
        }

        if (outerContainer == container || immune.Count <= 0)
            return; // The container we are intended to drop immune things to is the same container we are ashing everything in
                    //  it's a safe bet that we aren't ashing the container entity so there's no reason to eject anything from this container.

        // We need to get the immune things out of the container because the chances are we are about to ash the container, and we don't want them to get deleted despite their immunity.
        foreach (var entity in immune)
        {
            // Attempt to insert immune entities into innermost container at least as outer as outerContainer.
            var targetContainer = outerContainer;
            while (targetContainer != null)
            {
                if (_containerSystem.Insert(entity, targetContainer))
                    break;

                _containerSystem.TryGetContainingContainer((targetContainer.Owner, null, null), out targetContainer);
            }

            // If we couldn't or there was no container to insert into just dump them to the map/grid.
            if (targetContainer == null)
                _xformSystem.AttachToGridOrMap(entity);
        }
    }

    /// <summary>
    /// Makes a supermatter attempt to ash a given entity.
    /// </summary>
    /// <param name="hungry"></param>
    /// <param name="morsel"></param>
    /// <param name="sm"></param>
    /// <param name="outerContainer"></param>
    /// <returns></returns>
    private bool AttemptAshEntity(EntityUid hungry, EntityUid morsel, SupermatterComponent sm, BaseContainer? outerContainer = null)
    {
        if (!CanAshEntity(hungry, morsel, sm))
            return false;

        if (TryComp<PhysicsComponent>(morsel, out var phys))
        {
            if (phys.Mass == 0)
                return false;
        }

        if (Name(morsel) == "ash")
            return false;

        AshEntity(hungry, morsel, sm, outerContainer);
        return true;
    }

    /// <summary>
    /// Checks whether a supermatter can ash a given entity.
    /// </summary>
    /// <param name="hungry"></param>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    /// <returns></returns>
    private bool CanAshEntity(EntityUid hungry, EntityUid uid, SupermatterComponent sm)
    {
        var ev = new SupermatterAttemptConsumeEntityEvent(uid, hungry, sm);
        RaiseLocalEvent(uid, ref ev);
        return !ev.Cancelled;
    }

    /// <summary>
    /// Makes a supermatter ash a given entity.
    /// </summary>
    /// <param name="hungry"></param>
    /// <param name="morsel"></param>
    /// <param name="sm"></param>
    /// <param name="outerContainer"></param>
    private void AshEntity(EntityUid hungry, EntityUid morsel, SupermatterComponent sm, BaseContainer? outerContainer = null)
    {
        if (EntityManager.IsQueuedForDeletion(morsel)) // already handled, and we're substepping
            return;

        if (HasComp<MindContainerComponent>(morsel)
            || _tagSystem.HasTag(morsel, HighRiskItemTag))
        {
            _adminLogger.Add(LogType.EntityDelete, LogImpact.High, $"{ToPrettyString(morsel):player} entered the Supermatter of {ToPrettyString(hungry)} and was deleted");
        }

        QueueDel(morsel);
        var evSelf = new EntityAshedBySupermatterEvent(morsel, hungry, sm, outerContainer);
        var evEaten = new SupermatterConsumedEntityEvent(morsel, hungry, sm, outerContainer);
        RaiseLocalEvent(hungry, ref evSelf);
        RaiseLocalEvent(morsel, ref evEaten);
    }

    /// <summary>
    /// Adds power to the sm and adjust the integrity or AbsorptionHealingPool
    /// accordingly to whether the entity is alive or not.
    /// Also spawns an ash entity at the location of the ashed entity
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="smUid"></param>
    /// <param name="sm"></param>
    /// <param name="args"></param>
    private void OnAshed(EntityUid uid,SupermatterComponent sm, EntityAshedBySupermatterEvent args)
    {
        if (HasComp<MobStateComponent>(args.Entity))
        {
            var mob = Comp<MobStateComponent>(args.Entity);
            if (mob.CurrentState is not (MobState.Alive or MobState.Critical))
                return;

            if (!TryComp<MobSizeComponent>(args.Entity, out var size))
                return;
            var power = size.SizeProto?.SmPower ?? 0f;
            sm.PowerPool += power;
            sm.Integrity -= power / sm.IntegrityDivisor;
        }
        else
        {
            if (!TryComp<PhysicsComponent>(args.Entity, out var phys))
                return;
            if (phys.Mass == 0)
                return;
            sm.PowerPool += phys.Mass;
            sm.AbsorptionHealingPool += phys.Mass;
        }
        var coords = Transform(args.Entity).Coordinates;
        SpawnAtPosition("Ash", coords);

    }

    /// <summary>
    /// A generic event handler that prevents supermatters from ashing entities with a component of a given type if registered.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="comp"></param>
    /// <param name="args"></param>
    /// <typeparam name="TComp"></typeparam>
    private static void PreventConsume<TComp>(EntityUid uid, TComp comp, ref SupermatterAttemptConsumeEntityEvent args)
    {
        if (!args.Cancelled)
            args.Cancelled = true;
    }

    /// <summary>
    /// Recursively ash all entities within a container that is ashed by the supermatter.
    /// If an entity within an ashed container cannot be ashed itself it is removed from the container.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="comp"></param>
    /// <param name="args"></param>
    private void OnContainerConsumed(EntityUid uid, ContainerManagerComponent comp, ref SupermatterConsumedEntityEvent args)
    {
        var dropContainer = args.Container;
        if (dropContainer is null)
            _containerSystem.TryGetContainingContainer((uid, null, null), out dropContainer);

        foreach (var container in _containerSystem.GetAllContainers(uid))
        {
            AshEntitiesInContainer(args.SupermatterUid, container, args.Supermatter, dropContainer);
        }
    }
    /// <summary>
    /// Prevents two supermatters from annihilating one another.
    /// Specifically prevents supermatters from absorbing themselves.
    /// Also ensures that if this supermatter has already been absorbed by another supermatter it cannot be absorbed again.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="comp"></param>
    /// <param name="args"></param>
    private void OnAnotherSupermatterAttemptAbsorbThisSupermatter(EntityUid uid, SupermatterComponent comp, ref SupermatterAttemptConsumeEntityEvent args)
    {
        if (!args.Cancelled && (args.Supermatter == comp || comp.BeingAbsorbedByAnotherSupermatter))
            args.Cancelled = true;
    }

    /// <summary>
    /// Prevents two supermatters from annihilating one another.
    /// Specifically ensures if this supermatter is absorbed by another supermatter it knows that it has been absorbed.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="comp"></param>
    /// <param name="args"></param>
    private static void OnAnotherSupermatterAbsorbedThisSupermatter(EntityUid uid, SupermatterComponent comp, ref SupermatterConsumedEntityEvent args)
    {
        comp.BeingAbsorbedByAnotherSupermatter = true;
    }

    /// <summary>
    /// Handles supermatters deciding to escape containers they are inserted into.
    /// Delegates the actual escape to <see cref="OnSupermatterContained(SupermatterContainedEvent)" /> on a delay.
    /// This ensures that the escape is handled after all other handlers for the insertion event and satisfies the assertion that
    ///     the inserted entity SHALL be inside of the specified container after all handles to the entity event
    ///     <see cref="EntGotInsertedIntoContainerMessage" /> are processed.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="comp"></param>
    /// <param name="args"></param>
    private void OnSupermatterContained(EntityUid uid, SupermatterComponent comp, EntGotInsertedIntoContainerMessage args)
    {
        // Delegates processing an event until all queued events have been processed.
        QueueLocalEvent(new SupermatterContainedEvent(uid, comp, args));
    }

    /// <summary>
    /// Handles supermatters attempting to escape containers they have been inserted into.
    /// If the supermatter has not been absorbed by another supermatter this handles making the supermatter ash the containing
    ///     container and drop the the next innermost contaning container.
    /// This loops until the supermatter has escaped to the map or wound up in an indestructible container.
    /// </summary>
    /// <param name="args"></param>
    private void OnSupermatterContained(SupermatterContainedEvent args)
    {
        var uid = args.Entity;
        if (!Exists(uid))
            return;
        var comp = args.Supermatter;
        if (comp.BeingAbsorbedByAnotherSupermatter)
            return;

        var containerEntity = args.Args.Container.Owner;
        if (!Exists(containerEntity))
            return;
        if (AttemptAshEntity(uid, containerEntity, comp))
            return; // If we ash the entity we also ash everything in the containers it has.

        AshEntitiesInContainer(uid, args.Args.Container, comp, args.Args.Container);
    }
    #endregion

    /// <summary>
    /// Converts damage into power and
    /// scales radiation damage by the radiation damage multiplier so that it gives way more power
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    /// <param name="args"></param>
    private void OnDamage(EntityUid uid, SupermatterComponent sm, ref DamageChangedEvent args)
    {
        if (args.DamageDelta is null)
            return;
        var totalDamage = 0f;

        foreach (var (typeId, amount) in args.DamageDelta.DamageDict)
        {
            if (amount <= 0)
                continue;

            if (sm.RadiationDamageTypes.Contains(typeId))
                totalDamage += (float) amount * sm.RadiationDamageMultiplier;
            else
                totalDamage += (float) amount;
        }
        if (totalDamage <= 0)
            return;

        sm.PowerPool += totalDamage;
    }
}
