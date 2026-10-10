using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CombatExtended;
using HarmonyLib;
using SimpleSidearms.rimworld;
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
                var harmony = new Harmony(HarmonyId);
                harmony.PatchAll(typeof(Bootstrap).Assembly);
                AuditPatches();
                Log.Message(LogPrefix + "Patches installed.");
            }
            catch (Exception e)
            {
                Log.Error(LogPrefix + "Harmony patching failed: " + e);
            }
        }

        private static void AuditPatches()
        {
            var targets = new List<MethodBase>
            {
                AccessTools.Method(
                    typeof(JobGiver_RetrieveWeapon),
                    nameof(JobGiver_RetrieveWeapon.TryGiveJobStatic),
                    new[] { typeof(Pawn), typeof(bool) }),

                AccessTools.Method(
                    typeof(Utility_HoldTracker),
                    nameof(Utility_HoldTracker.GetExcessThing),
                    new[]
                    {
                        typeof(Pawn),
                        typeof(Thing).MakeByRefType(),
                        typeof(int).MakeByRefType()
                    }),

                AccessTools.Method(
                    typeof(Utility_HoldTracker),
                    nameof(Utility_HoldTracker.GetStorageByThingDef),
                    new[] { typeof(Pawn) }),

                AccessTools.Method(
                    typeof(Utility_HoldTracker),
                    nameof(Utility_HoldTracker.GetExcessEquipment),
                    new[]
                    {
                        typeof(Pawn),
                        typeof(ThingWithComps).MakeByRefType()
                    }),

                AccessTools.Method(
                    typeof(Pawn),
                    nameof(Pawn.GetGizmos),
                    Type.EmptyTypes)
            };

            int found = 0;

            foreach (MethodBase target in targets)
            {
                if (target == null)
                {
                    Log.Error(LogPrefix +
                        "Patch audit could not resolve an expected target method. " +
                        "CE, Simple Sidearms, or RimWorld may have changed.");
                    continue;
                }

                Patches info = Harmony.GetPatchInfo(target);
                bool owned = info != null &&
                    info.Prefixes.Concat(info.Postfixes)
                        .Concat(info.Transpilers)
                        .Concat(info.Finalizers)
                        .Any(patch => patch.owner == HarmonyId);

                if (!owned)
                {
                    Log.Error(LogPrefix +
                        "Patch audit found no installed lmaodog patch on " +
                        target.DeclaringType?.FullName + "." + target.Name + ".");
                    continue;
                }

                found++;
            }

            if (found != targets.Count)
            {
                Log.Error(LogPrefix +
                    "Patch audit failed: " + found + "/" + targets.Count +
                    " expected target methods are patched.");
            }
            else
            {
                Log.Message(LogPrefix +
                    "Patch audit passed (" + found + "/" + targets.Count + ").");
            }
        }
    }
}
