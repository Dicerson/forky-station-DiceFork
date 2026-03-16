using Content.Server._Funkystation.SM.Components;
using Content.Server._Funkystation.SM.Events;
using Content.Shared._Funkystation.SM.Components;

namespace Content.Server._Funkystation.SM.EntitySystems;

public sealed class SupermatterDelamSystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<SupermatterComponent, SupermatterDelaminationEvent>(OnDelam);
    }

    private void OnDelam(EntityUid uid, SupermatterComponent sm, ref SupermatterDelaminationEvent args)
    {
        switch (args.DominantCharacteristic)
        {
            case GasCharacteristicsType.Growth:
                TriggerSingularity(uid, sm);
                break;

            case GasCharacteristicsType.Conductivity:
                TriggerTesla(uid, sm);
                break;

            case GasCharacteristicsType.Enthalpy:
                TriggerExplosion(uid, sm);
                break;

            case GasCharacteristicsType.Stability:
                TriggerCascade(uid, sm);
                break;
        }
    }

    private void TriggerSingularity(EntityUid uid, SupermatterComponent sm)
    {

    }

    private void TriggerTesla(EntityUid uid, SupermatterComponent sm)
    {

    }

    private void TriggerExplosion(EntityUid uid, SupermatterComponent sm)
    {

    }

    private void TriggerCascade(EntityUid uid, SupermatterComponent sm)
    {

    }
}
