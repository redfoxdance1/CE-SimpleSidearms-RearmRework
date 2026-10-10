using HarmonyLib;
using Verse;

namespace CESimpleSidearmsRearm
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        public const string HarmonyId = "Zynko.CESimpleSidearmsRearm";
        public const string LogPrefix = "[CE+SS Rearm] ";

        static Bootstrap()
        {
            if (!ModsConfig.IsActive("CETeam.CombatExtended") ||
                !ModsConfig.IsActive("PeteTimesSix.SimpleSidearms"))
                return;

            if (ModsConfig.IsActive("eebette.CESimpleSidearmsCompat") ||
                ModsConfig.IsActive("eebette.CESimpleSidearmsCompat.Loadouts"))
                Log.Warning(LogPrefix + "Another CE + Simple Sidearms compatibility suite is active; overlapping patches may conflict.");

            new Harmony(HarmonyId).PatchAll();
            Log.Message(LogPrefix + "Patches installed.");
        }
    }
}