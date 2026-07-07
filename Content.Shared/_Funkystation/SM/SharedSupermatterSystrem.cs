using Content.Server._Funkystation.SM.Events;
using Content.Shared._Funkystation.Mobs;
using Content.Shared._Funkystation.SM.Components;
using Content.Shared.Administration.Logs;
using Content.Shared.Chat.Prototypes;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Damage.Systems;
using Content.Shared.Database;
using Content.Shared.Interaction;
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

namespace Content.Shared._Funkystation.SM;

public abstract partial class SharedSupermatterSystem : EntitySystem
{

    [Dependency] private readonly ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly MetaDataSystem _metaDataSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedContainerSystem _containerSystem = default!;
    [Dependency] private readonly SharedTransformSystem _xformSystem = default!;
    [Dependency] private readonly TagSystem _tagSystem = default!;


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
        SubscribeLocalEvent<SharedSupermatterComponent, DamageChangedEvent>(OnDamage);
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
    /// Modified to take the SM component and save the audio process on the SM component for later use
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="sm"></param>
    /// <param name="proto"></param>
    /// <param name="emoteId"></param>
    /// <param name="audioParams"></param>
    /// <returns></returns>
    public void TryPlayEmoteSound(EntityUid uid, SharedSupermatterComponent sm, EmoteSoundsPrototype? proto, string emoteId, AudioParams? audioParams = null)
    {
        if (proto == null)
            return;

        // try to get specific sound for this emote
        if (!proto.Sounds.TryGetValue(emoteId, out var sound))
        {
            // no specific sound - check fallback
            sound = proto.FallbackSound;
            if (sound == null)
                return;
        }

        // optional override params > general params for all sounds in set > individual sound params
        var param = audioParams ?? proto.GeneralParams ?? sound.Params;
        sm.MobAudioProcess = _audio.PlayPvs(sound, uid, param)?.Entity;
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

        if (TryComp<MobStateComponent>(morsel, out var mob))
        {
            if (mob.CurrentState is (MobState.Alive or MobState.Critical))
            {
                if (TryComp<VocalComponent>(morsel, out var vocal) && vocal.EmoteSounds is { } sounds)
                {
                    EnsureComp<AshedBySupermatterComponent>(morsel);
                    TryPlayEmoteSound(hungry, sm, _proto.Index(sounds), vocal.ScreamId);
                    Robust.Shared.Timing.Timer.Spawn(TimeSpan.FromSeconds(sm.ScreamCutOffTimer),
                        () =>
                        {
                            if (sm.MobAudioProcess != null)
                                _audio.Stop(sm.MobAudioProcess);
                            QueueDel(morsel);
                            var evSelf = new EntityAshedBySupermatterEvent(morsel, hungry, sm, outerContainer, fromTree, isMob);
                            var evEaten = new SupermatterAshedEntityEvent(morsel, hungry, sm, outerContainer, fromTree, isMob );
                            RaiseLocalEvent(hungry, ref evSelf);
                            RaiseLocalEvent(morsel, ref evEaten);
                        });
                    return;
                }
            }
        }

        QueueDel(morsel);
        var evSelf = new EntityAshedBySupermatterEvent(morsel, hungry, sm, outerContainer, fromTree, isMob);
        var evEaten = new SupermatterAshedEntityEvent(morsel, hungry, sm, outerContainer, fromTree, isMob );
        RaiseLocalEvent(hungry, ref evSelf);
        RaiseLocalEvent(morsel, ref evEaten);


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
                if (HasComp<SolutionContainerManagerComponent>(entity))
                    continue;
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
                    if (HasComp<SolutionContainerManagerComponent>(entity))
                        continue;
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
                if (HasComp<SolutionContainerManagerComponent>(entity)) // Ideally this check is not needed but because morsel does not get filtered it's needed
                    ashedCount -= 1;

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
    private void OnDamage(EntityUid uid, SharedSupermatterComponent sm, ref DamageChangedEvent args)
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

        sm.Activated = true;
        sm.PowerPool += totalDamage;
    }

    private void OnEmbed(EntityUid uid, SharedSupermatterComponent sm, ref ThrowHitByEvent args)
    {

        AttemptAshEntity(args.Target, args.Thrown, sm);
    }
}
