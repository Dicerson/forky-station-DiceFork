using Content.Shared._Funkystation.SM.Components;

namespace Content.Server._Funkystation.SM.Events;
[ByRefEvent]
public readonly record struct SupermatterDelaminationEvent(EntityUid supermatterUid, GasCharacteristicsType dominantCharacteristic)
{
    /// <summary>
    /// The Id of the supermatter that is delaminating
    /// </summary>
    public readonly EntityUid SupermatterUid = supermatterUid;

    /// <summary>
    /// The component belonging to the supermatter
    /// </summary>
    public readonly GasCharacteristicsType DominantCharacteristic = dominantCharacteristic;

}

