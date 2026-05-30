using System.Collections.Generic;

namespace RobustoClient.Systems.AutoChem;

public interface IChemState
{
    IChemState Execute(float frameTime, RobustaAutoChemSystem system);
    string GetStatusInfo(RobustaAutoChemSystem system);
    float GetProgress(RobustaAutoChemSystem system);
}
