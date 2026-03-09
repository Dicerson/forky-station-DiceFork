using Content.Server._Funkystation.SM.Components;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Shared._Funkystation.SM;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._Funkystation.SM.EntitySystems;

public sealed class SupermatterSystem : EntitySystem
{
    [Dependency] private readonly AtmosphereSystem _atmosphereSystem = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly MapSystem _mapSystem = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly TransformSystem _xformSystem = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SupermatterComponent, AtmosDeviceUpdateEvent>(OnProcessSupermatter);
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
        AbsorbGas(uid, sm);
        ComputeGasCharacteristics (sm);
        // …rest of the tick logic
    }

    /// <summary>
    /// Absorbs gas in a 3 x 3 area with the Supermatter at the center
    /// </summary>
    /// <param name="smUid"></param>
    /// <param name="sm"></param>
    private void AbsorbGas(EntityUid smUid, SupermatterComponent sm)
    {
        var xform = Transform(smUid);


        var mapUid = xform.MapUid;
        var mapCoords = _xformSystem.GetMapCoordinates(smUid);
        if (!_mapManager.TryFindGridAt(mapCoords, out var gridUid, out var gridComp))
            return; // In space

        Entity<GridAtmosphereComponent?, GasTileOverlayComponent?>? gridEnt = null;
        if (TryComp(gridUid, out GridAtmosphereComponent? gridAtmos))
        {
            TryComp(gridUid, out GasTileOverlayComponent? overlay);
            gridEnt = new Entity<GridAtmosphereComponent?, GasTileOverlayComponent?>(
                gridUid,
                gridAtmos,
                overlay
            );
        }

        Entity<MapAtmosphereComponent?>? mapEnt = null;

        if (mapUid is { } map && TryComp(map, out MapAtmosphereComponent? mapAtmos))
        {
            mapEnt = new Entity<MapAtmosphereComponent?>(map, mapAtmos);
        }

        if (gridEnt == null)
            return;

        var centerTile = _mapSystem.WorldToTile(gridUid, gridComp, mapCoords.Position);

        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                var tile = centerTile + new Vector2i(dx, dy);

                var mixture = _atmosphereSystem.GetTileMixture(
                    gridEnt,
                    mapEnt,
                    tile,
                    excite: false
                );

                if (mixture == null)
                    continue;

                var absorbed = mixture.RemoveRatio(0.05f);
                for (var i = 0; i < Atmospherics.AdjustedNumberOfGases; i++)
                {
                    var amount = absorbed.GetMoles(i);
                    sm.AbsorbedGas.AdjustMoles(i, amount);
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

        // Normalize as per your spec
        sm.Stability    = stability / 100f;
        sm.Growth       = growth / 100f;
        sm.Conductivity = conductivity / 100f;
        sm.Enthalpy     = enthalpy / 100f;
    }




}
