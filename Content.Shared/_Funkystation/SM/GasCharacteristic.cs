using Content.Shared._Funkystation.SM.Prototypes;
using Content.Shared.Atmos;
using Robust.Shared.Prototypes;

namespace Content.Shared._Funkystation.SM;

public readonly record struct GasCharacteristics(
    float Stability,
    float Growth,
    float Conductivity,
    float Enthalpy
);

public static class GasCharacteristicData
{
    public static readonly Dictionary<Gas, GasCharacteristics> GasTable = new();

    public static void LoadFromPrototypes(IPrototypeManager proto)
    {
        GasTable.Clear();

        foreach (var p in proto.EnumeratePrototypes<GasCharacteristicsPrototype>())
        {
            if (!Enum.TryParse<Gas>(p.ID, out var gas))
                continue;

            GasTable[gas] = new GasCharacteristics(
                Stability: p.Stability,
                Growth: p.Growth,
                Conductivity: p.Conductivity,
                Enthalpy: p.Enthalpy
            );
        }
    }

}



