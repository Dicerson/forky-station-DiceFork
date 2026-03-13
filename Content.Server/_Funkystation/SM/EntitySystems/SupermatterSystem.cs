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
    [Dependency] private readonly TransformSystem _transformSystem = default!;

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
        AbsorbGas(uid, sm, args);
        ComputeGasCharacteristics (sm);
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
                    continue;

                var absorbed = mixture.RemoveRatio(ratio);
                for (var gas = 0; gas < Atmospherics.AdjustedNumberOfGases; gas++)
                {
                    var amount = absorbed.GetMoles(gas);
                    sm.AbsorbedGas.AdjustMoles(gas, amount);
                    absorbed.SetMoles(gas, 0f);
                }
                _atmosphereSystem.Merge(mixture, absorbed);
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




}
