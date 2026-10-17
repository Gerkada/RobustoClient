using RobustoClient.Systems.Abstract;
using Content.Shared.Antag;
using Content.Shared.Overlays;

namespace RobustoClient.Systems;

public class RobustaJobIconsSystem : LocalPlayerAddCompSystem<ShowJobIconsComponent>;
public class RobustaCriminalRecordIcons : LocalPlayerAddCompSystem<ShowCriminalRecordIconsComponent>;
public class RobustaMindShieldIcons : LocalPlayerAddCompSystem<ShowMindShieldIconsComponent>;
public class RobustaSyndicateIcons : LocalPlayerAddCompSystem<ShowSyndicateIconsComponent>;
public class RobustaAntagIcons : LocalPlayerAddCompSystem<ShowAntagIconsComponent>;