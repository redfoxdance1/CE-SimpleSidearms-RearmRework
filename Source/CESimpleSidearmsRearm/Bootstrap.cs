using System;
using HarmonyLib;
using Verse;

namespace CESimpleSidearmsRearm
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        public const string HarmonyId = "redfoxdance1.CESimpleSidearmsRearm";
        public const string LogPrefix = "[CE+SS Rearm] ";

        static Bootstrap()
        {
            if (!ModsConfig.IsActive("CETeam.CombatExtended") ||
                !ModsConfig.IsActive("PeteTimesSix.SimpleSidearms"))
            {
                Log.Warning(LogPrefix +
                    "Combat Extended and Simple Sidearms must both be active; no patches were installed.");
                return;
            }

            if (ModsConfig.IsActive("eebette.CESimpleSidearmsCompat") ||
                ModsConfig.IsActive("eebette.CESimpleSidearmsCompat.Loadouts"))
            {
                Log.Error(LogPrefix +
                    "An overlapping CE + Simple Sidearms compatibility suite is active. " +
                    "To avoid double-patching loadout behavior, this mod will not install its patches.");
                return;
            }

            try
            {
                new Harmony(HarmonyId).PatchAll(typeof(Bootstrap).Assembly);
                Log.Message(LogPrefix + "Patches installed.");
            }
            catch (Exception e)
            {
                Log.Error(LogPrefix + "Harmony patching failed: " + e);
            }
        }
    }
}
