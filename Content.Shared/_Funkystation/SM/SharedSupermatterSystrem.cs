using Content.Server._Funkystation.SM.Events;
using Content.Shared._Funkystation.Mobs;
using Content.Shared._Funkystation.SM.Components;
using Content.Shared.Administration.Logs;
using Content.Shared.Chat.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.Database;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Mind.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Projectiles;
using Content.Shared.Speech.Components;
using Content.Shared.Stacks;
using Content.Shared.Station.Components;
using Content.Shared.Tag;
using Content.Shared.Throwing;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Shared._Funkystation.SM;

public abstract partial class SharedSupermatterSystem : EntitySystem
{

    [Dependency] private ISharedAdminLogManager _adminLogger = default!;
    //[Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private MetaDataSystem _metaDataSystem = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedContainerSystem _containerSystem = default!;
    [Dependency] private SharedTransformSystem _xformSystem = default!;
    [Dependency] private TagSystem _tagSystem = default!;
    [Dependency] private InventorySystem _inventory = default!;



    private static readonly ProtoId<TagPrototype> HighRiskItemTag = "HighRiskItem";
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MapGridComponent, SupermatterAttemptConsumeEntityEvent>(PreventAshing);
        SubscribeLocalEvent<StationDataComponent, SupermatterAttemptConsumeEntityEvent>(PreventAshing);
        SubscribeLocalEvent<ProjectileComponent, SupermatterAttemptConsumeEntityEvent>(PreventAshingProjectile);
        SubscribeLocalEvent<SharedSupermatterComponent, EntGotInsertedIntoContainerMessage>(OnSupermatterContained);
        SubscribeLocalEvent<SupermatterContainedEvent>(OnSupermatterContained);
        SubscribeLocalEvent<SharedSupermatterComponent, SupermatterAttemptConsumeEntityEvent>(OnAnotherSupermatterAttemptAbsorbThisSupermatter);
        SubscribeLocalEvent<SharedSupermatterComponent, SupermatterAshedEntityEvent>(OnAnotherSupermatterAbsorbedThisSupermatter);
        SubscribeLocalEvent<SharedSupermatterComponent, EntityAshedBySupermatterEvent>(OnAshed);
        SubscribeLocalEvent<SharedSupermatterComponent, StartCollideEvent>(OnAshAbsorption);
        SubscribeLocalEvent<ContainerManagerComponent, SupermatterAshedEntityEvent>(OnContainerAshed);
        SubscribeLocalEvent<SharedSupermatterComponent, DamageDealtEvent>(OnDamage);
        SubscribeLocalEvent<SharedSupermatterComponent, ThrowHitByEvent>(OnEmbed);
        SubscribeLocalEvent<SharedSupermatterComponent, InteractHandEvent>(OnInteractHand);
        SubscribeLocalEvent<SharedSupermatterComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<SupermatterImmuneComponent, SupermatterAttemptConsumeEntityEvent>(OnImmuneCancelAshing);

    }

    private void OnInteractHand(EntityUid uid, SharedSupermatterComponent sm, InteractHandEvent args)
    {
        if (args.Handled)
            return;

        if (HasComp<SupermatterImmuneComponent>(args.User))
            return;

        if (_inventory.TryGetSlotEntity(args.User, "outerClothing", out var suitUid))
        {
            if (HasComp<SupermatterImmuneComponent>(suitUid))
                return;
        }

        if (_inventory.TryGetSlotEntity(args.User, "head", out var helmetUid))
        {
            if (HasComp<SupermatterImmuneComponent>(helmetUid))
                return;
        }

        if (!AttemptAshEntity(uid, args.User, sm))
            return;

        args.Handled = true;
    }

    private void OnInteractUsing(EntityUid uid, SharedSupermatterComponent sm, InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        if (!AttemptAshEntity(uid, args.Used, sm))
            return;

        args.Handled = true;
    }

    private void OnImmuneCancelAshing(EntityUid uid, SupermatterImmuneComponent _, ref SupermatterAttemptConsumeEntityEvent args)
    {
        args.Cancelled = true;
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
    private void OnAshAbsorption(EntityUid uid, SharedSupermatterComponent sm, ref StartCollideEvent args)
    {
        AttemptAshEntity(uid, args.OtherEntity, sm);
    }


    /// <summary>
    /// Makes a supermatter attempt to ash a given entity.
    /// </summary>
    /// <param name="hungry"></param>
    /// <param name="morsel"></param>
    /// <param name="sm"></param>
    /// <param name="outerContainer"></param>
    /// <param name="fromTree"></param>
    /// <param name="isMob"></param>
    /// <returns></returns>
    private bool AttemptAshEntity(EntityUid hungry, EntityUid morsel, SharedSupermatterComponent sm, BaseContainer? outerContainer = null, bool fromTree = false, bool isMob = false)
    {
        if (!CanAshEntity(hungry, morsel, sm))
            return false;

        if (TryComp<PhysicsComponent>(morsel, out var phys))
        {
            if (phys.Mass == 0)
                return false;
        }

        if (HasComp<AshedBySupermatterComponent>(morsel))
            return false;

        if (Name(morsel) == "ash")
            return false;

        AshEntity(hungry, morsel, sm, outerContainer, fromTree, isMob);
        return true;
    }

    /// <summary>
    /// Checks whether a supermatter can ash a given entity.
    /// </summary>
    /// <param name="hungry"></param>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    /// <returns></returns>
    private bool CanAshEntity(EntityUid hungry, EntityUid uid, SharedSupermatterComponent sm)
    {
        var ev = new SupermatterAttemptConsumeEntityEvent(uid, hungry, sm);
        RaiseLocalEvent(uid, ref ev);
        return !ev.Cancelled;
    }

    /// <summary>
    /// Modified version of the TryPlayEmoteSound from SharedChatSystem.
    /// Modified to return the soundSpecifier so it can be played after the mob it was from got deleted
    /// </summary>
    /// <param name="proto"></param>
    /// <param name="emoteId"></param>
    /// <returns></returns>
    public SoundSpecifier? TryGetEmoteSound(EmoteSoundsPrototype? proto, string emoteId)
    {
        if (proto == null)
            return null;

        // try to get specific sound for this emote
        if (!proto.Sounds.TryGetValue(emoteId, out var sound))
        {
            // no specific sound - check fallback
            sound = proto.FallbackSound;
            if (sound == null)
                return null;
        }

        return sound;
        // optional override params > general params for all sounds in set > individual sound params
        //var param = audioParams ?? proto.GeneralParams ?? sound.Params;
        //sm.MobAudioProcess = _audio.PlayPvs(sound, uid, param)?.Entity;
    }
    /// <summary>
    /// Raises the supermatter ash event on an entity that got ashed and the supermatter,
    /// so an ash stack can be created.
    /// </summary>
    /// <param name="morsel"></param>
    /// <param name="hungry"></param>
    /// <param name="sm"></param>
    /// <param name="outerContainer"></param>
    /// <param name="fromTree"></param>
    /// <param name="isMob"></param>
    private void RaiseSupermatterAshEvents(EntityUid morsel, EntityUid hungry, SharedSupermatterComponent sm, BaseContainer? outerContainer, bool fromTree, bool isMob)
    {
        var evSelf = new EntityAshedBySupermatterEvent(morsel, hungry, sm, outerContainer, fromTree, isMob);
        var evEaten = new SupermatterAshedEntityEvent(morsel, hungry, sm, outerContainer, fromTree, isMob);

        RaiseLocalEvent(hungry, ref evSelf);
        RaiseLocalEvent(morsel, ref evEaten);
    }
    /// <summary>
    /// deletes an entity and calls the raise supermatter ash event
    /// </summary>
    /// <param name="morsel"></param>
    /// <param name="hungry"></param>
    /// <param name="sm"></param>
    /// <param name="outerContainer"></param>
    /// <param name="fromTree"></param>
    /// <param name="isMob"></param>
    private void DeleteAndRaise(EntityUid morsel, EntityUid hungry, SharedSupermatterComponent sm, BaseContainer? outerContainer, bool fromTree, bool isMob)
    {
        QueueDel(morsel);
        RaiseSupermatterAshEvents(morsel, hungry, sm, outerContainer, fromTree, isMob);
    }


    /// <summary>
    /// Makes a supermatter ash a given entity.
    /// </summary>
    /// <param name="hungry"></param>
    /// <param name="morsel"></param>
    /// <param name="sm"></param>
    /// <param name="outerContainer"></param>
    /// <param name="fromTree"></param>
    /// <param name="isMob"></param>
    private void AshEntity(EntityUid hungry, EntityUid morsel, SharedSupermatterComponent sm, BaseContainer? outerContainer = null, bool fromTree = false, bool isMob = false)
    {
        if (EntityManager.IsQueuedForDeletion(morsel)) // already handled, and we're substepping
            return;

        if (HasComp<MindContainerComponent>(morsel)
            || _tagSystem.HasTag(morsel, HighRiskItemTag))
        {
            _adminLogger.Add(LogType.EntityDelete, LogImpact.High, $"{ToPrettyString(morsel):player} entered the Supermatter of {ToPrettyString(hungry)} and was deleted");
        }

        // Early return: not a mob
        if (!TryComp<MobStateComponent>(morsel, out var mob))
        {
            DeleteAndRaise(morsel, hungry, sm, outerContainer, fromTree, isMob);
            return;
        }

        // Early return: not alive or critical
        if (mob.CurrentState is not (MobState.Alive or MobState.Critical))
        {
            DeleteAndRaise(morsel, hungry, sm, outerContainer, fromTree, isMob);
            return;
        }

        // Early return: no vocal component
        if (!TryComp<VocalComponent>(morsel, out var vocal) || vocal.EmoteSounds is not { } sounds)
        {
            DeleteAndRaise(morsel, hungry, sm, outerContainer, fromTree, isMob);
            return;
        }

        // Early return: no scream sound
        var mobScream = TryGetEmoteSound(_proto.Index(sounds), vocal.ScreamId);
        if (mobScream == null)
        {
            DeleteAndRaise(morsel, hungry, sm, outerContainer, fromTree, isMob);
            return;
        }

        // HUMANOID BRANCH
        if (TryComp<HumanoidProfileComponent>(morsel, out _))
        {
            var coords = Transform(morsel).Coordinates;
            if (!TryComp<HideableHumanoidLayersComponent>(morsel, out var hideComp))
                DeleteAndRaise(morsel, hungry, sm, outerContainer, fromTree, isMob);
            if (hideComp != null)
            {
                // Hide all relevant layers
                foreach (HumanoidVisualLayers layer in Enum.GetValues(typeof(HumanoidVisualLayers)))
                {
                    // Mark this layer as hidden by "None" or some synthetic SlotFlags
                    hideComp.HiddenLayers[layer] = SlotFlags.NONE;

                    // Raise the visibility changed event so the client updates visuals
                    var ev = new HumanoidLayerVisibilityChangedEvent(layer, false);
                    RaiseLocalEvent(morsel, ref ev);
                }
                SpawnAtPosition("SupermatterAshingEffect", coords);
            }


            var param = _proto.Index(sounds).GeneralParams ?? mobScream.Params;

            sm.MobAudioProcess = _audio.PlayPvs(mobScream, hungry, param)?.Entity;
            Timer.Spawn(TimeSpan.FromSeconds(0.75),
                () =>
                {
                    DeleteAndRaise(morsel, hungry, sm, outerContainer, fromTree, isMob);
                });
            Timer.Spawn(TimeSpan.FromSeconds(sm.ScreamCutOffTimer),
                () =>
                {
                    if (sm.MobAudioProcess != null)
                        _audio.Stop(sm.MobAudioProcess);
                });


            return;
        }

        // NON-HUMANOID BRANCH
        var param2 = _proto.Index(sounds).GeneralParams ?? mobScream.Params;
        sm.MobAudioProcess = _audio.PlayPvs(mobScream, hungry, param2)?.Entity;

        Timer.Spawn(TimeSpan.FromSeconds(sm.ScreamCutOffTimer),
            () =>
            {
                if (sm.MobAudioProcess != null)
                    _audio.Stop(sm.MobAudioProcess);

                DeleteAndRaise(morsel, hungry, sm, outerContainer, fromTree, isMob);
            });
    }

    /// <summary>
    /// Adds power to the sm and adjust the integrity or AbsorptionHealingPool
    /// accordingly to whether the entity is alive or not.
    /// Also spawns an ash entity at the location of the ashed entity
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    /// <param name="args"></param>
    private void OnAshed(EntityUid uid, SharedSupermatterComponent sm, EntityAshedBySupermatterEvent args)
    {
        sm.Activated = true;
        int count = 1;
        if (TryComp<MobStateComponent>(args.Entity, out var mob))
        {
            _audio.PlayPvs(sm.SoundAsh, uid);
            if (mob.CurrentState is not (MobState.Alive or MobState.Critical))
                return;

            if (!TryComp<MobSizeComponent>(args.Entity, out var size))
                return;

            var power = size.SizeProto?.SmPower ?? 0f;
            sm.Power += power;
            sm.Integrity -= power / sm.IntegrityDivisor;
        }
        else
        {

            if(args is { FromContainerTree: false, IsMob: false })
                _audio.PlayPvs(sm.SoundAsh, uid);

            else if(!_audio.IsPlaying(sm.AudioProcess) && !args.IsMob)
                sm.AudioProcess = _audio.PlayPvs(sm.SoundAsh, uid)?.Entity;

            if (TryComp<StackComponent>(args.Entity, out var stack))
                count = stack.Count;

            if (!TryComp<PhysicsComponent>(args.Entity, out var phys))
                return;

            if (phys.Mass == 0)
                return;

            sm.PowerPool += phys.Mass * count;
            sm.AbsorptionHealingPool += phys.Mass * count;
        }

        if (args.FromContainerTree || HasComp<ContainerManagerComponent>(args.Entity) )
            return;

        var coords = Transform(args.Entity).Coordinates;
        var ash = SpawnAtPosition("Ash", coords);

        if (count > 1)
        {
            var meta = MetaData(ash);
            var baseDesc = meta.EntityDescription;
            var newDesc = $"{baseDesc} It contains the remains of {count} things.";
            _metaDataSystem.SetEntityDescription(ash, newDesc, meta);
        }
    }

    /// <summary>
    /// A generic event handler that prevents supermatters from ashing entities with a component of a given type if registered.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="comp"></param>
    /// <param name="args"></param>
    /// <typeparam name="TComp"></typeparam>
    private static void PreventAshing<TComp>(EntityUid uid, TComp comp, ref SupermatterAttemptConsumeEntityEvent args)
    {
        if (!args.Cancelled)
            args.Cancelled = true;
    }

    private void PreventAshingProjectile(EntityUid uid, ProjectileComponent comp, ref SupermatterAttemptConsumeEntityEvent args)
    {
        if (HasComp<EmbeddableProjectileComponent>(uid))
            return;
        args.Cancelled = true;
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

        AshContainerTree(uid, containerEntity, comp, args.Args.Container);
    }

    /// <summary>
    /// Recursively ash all entities within a container that is ashed by the supermatter.
    /// If an entity within an ashed container cannot be ashed itself it is removed from the container.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="comp"></param>
    /// <param name="args"></param>
    private void OnContainerAshed(EntityUid uid, ContainerManagerComponent comp, ref SupermatterAshedEntityEvent args)
    {
        if (args.Container != null)
            return;

        var dropContainer = args.Container;
        if (dropContainer is null)
            _containerSystem.TryGetContainingContainer((uid, null, null), out dropContainer);

        AshContainerTree(args.SupermatterUid, args.Entity, args.Supermatter, dropContainer);
    }

    /// <summary>
    /// Makes a list of all entities in the container tree
    /// </summary>
    /// <param name="hungry"></param>
    /// <param name="morsel"></param>
    /// <param name="sm"></param>
    /// <param name="outerContainer"></param>
    private void AshContainerTree(EntityUid hungry, EntityUid morsel, SharedSupermatterComponent sm, BaseContainer? outerContainer)
    {
        if (_containerSystem.TryGetContainingContainer((morsel, null, null), out var parent))
            return;

        List<BaseContainer> allContainers = new();
        CollectAllContainers(morsel, allContainers);

        List<EntityUid> allEntities = new();
        allEntities.Add(morsel);
        CollectAllEntities(allContainers, allEntities);

        // Step 3: Ash them
        AshCollectedEntities(hungry, sm, outerContainer, morsel, allEntities);
    }

    /// <summary>
    /// RECURSION ALERT
    /// Recursive Depth‑First Search of all containers
    /// We love Recursion
    /// Explores the container tree as deep as possible before backing up and going down the next branch on the tree.
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="results"></param>
    private void CollectAllContainers(EntityUid uid, List<BaseContainer> results)
    {
        if (!HasComp<ContainerManagerComponent>(uid))
            return;
        foreach (var container in _containerSystem.GetAllContainers(uid))
        {
            results.Add(container);

            foreach (var entity in container.ContainedEntities)
            {
                CollectAllContainers(entity, results);
            }
        }
    }

    /// <summary>
    /// Iterative Depth‑First Search of all containers.
    /// Explores the container tree as deep as possible before backing up and going down the next branch on the tree.
    /// </summary>
    /// <param name="root"></param>
    /// <param name="results"></param>
    private void CollectAllContainersIterative(EntityUid root, List<BaseContainer> results)
    {
        Stack<EntityUid> stack = new();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var uid = stack.Pop();

            foreach (var container in _containerSystem.GetAllContainers(uid))
            {
                results.Add(container);

                foreach (var entity in container.ContainedEntities)
                {
                    stack.Push(entity);
                }
            }
        }
    }


    /// <summary>
    /// Finds all the entities in a list of containers
    /// </summary>
    /// <param name="containers"></param>
    /// <param name="results"></param>
    private void CollectAllEntities(List<BaseContainer> containers, List<EntityUid> results)
    {
        foreach (var container in containers)
        {
            foreach (var entity in container.ContainedEntities)
            {
                results.Add(entity);
            }
        }
    }

    /// <summary>
    /// Ashes all entities in a list of entities
    /// </summary>
    /// <param name="hungry"></param>
    /// <param name="sm"></param>
    /// <param name="outerContainer"></param>
    /// <param name="morsel"></param>
    /// <param name="allEntities"></param>
    private void AshCollectedEntities(EntityUid hungry, SharedSupermatterComponent sm, BaseContainer? outerContainer, EntityUid morsel, List<EntityUid> allEntities)
    {
        List<EntityUid> immune = new();
        var ashedCount = 0;
        var baseIsMob = false;
        if(HasComp<MobStateComponent>(morsel))
            baseIsMob = true;
        foreach (var entity in allEntities)
        {
            if (entity == hungry || !AttemptAshEntity(hungry, entity, sm, outerContainer, fromTree: true,  isMob: baseIsMob))
            {
                // The first check keeps supermatters an admin smited into a locker from ashing themselves.
                // The second check keeps things that have been rendered immune to supermatters from being deleted by a supermatter eating their container.
                immune.Add(entity);
                continue;
            }
            if (TryComp<StackComponent>(entity, out var stack))
            {
                ashedCount += stack.Count;
            }
            else
            {
                ashedCount++;
            }

        }
        if (ashedCount  > 0)
        {
            var coords = Transform(morsel).Coordinates;
            var ash = SpawnAtPosition("Ash", coords);

            // Set description
            if (ashedCount > 1)
            {
                var meta = MetaData(ash);
                var baseDesc = meta.EntityDescription; // "This used to be something, but now it's not."
                var newDesc = $"{baseDesc} It contains the remains of {ashedCount} things.";
                _metaDataSystem.SetEntityDescription(ash,newDesc, meta);
            }
        }

        // Eject immune items if needed
        foreach (var entity in immune)
        {
            var target = outerContainer;

            while (target != null)
            {
                if (_containerSystem.Insert(entity, target))
                    break;

                _containerSystem.TryGetContainingContainer((target.Owner, null, null), out target);
            }

            if (target == null)
                _xformSystem.AttachToGridOrMap(entity);
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
    private void OnAnotherSupermatterAttemptAbsorbThisSupermatter(EntityUid uid, SharedSupermatterComponent comp, ref SupermatterAttemptConsumeEntityEvent args)
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
    private static void OnAnotherSupermatterAbsorbedThisSupermatter(EntityUid uid, SharedSupermatterComponent comp, ref SupermatterAshedEntityEvent args)
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
    private void OnSupermatterContained(EntityUid uid, SharedSupermatterComponent comp, EntGotInsertedIntoContainerMessage args)
    {
        // Delegates processing an event until all queued events have been processed.
        QueueLocalEvent(new SupermatterContainedEvent(uid, comp, args));
    }


    #endregion

    /// <summary>
    /// Converts damage into power and
    /// scales radiation damage by the radiation damage multiplier so that it gives way more power
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    /// <param name="args"></param>
    private void OnDamage(EntityUid uid, SharedSupermatterComponent sm, ref DamageDealtEvent args)
    {
        if (args.Damage.GetTotal() == 0)
            return;
        var totalDamage = 0f;

        foreach (var (typeId, amount) in args.Damage.DamageDict)
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

        sm.Activated = true;
        sm.PowerPool += totalDamage;
    }

    private void OnEmbed(EntityUid uid, SharedSupermatterComponent sm, ref ThrowHitByEvent args)
    {

        AttemptAshEntity(args.Target, args.Thrown, sm);
    }
}
