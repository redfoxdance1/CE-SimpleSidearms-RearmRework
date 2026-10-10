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

        public static bool Enabled { get; private set; }

        static Bootstrap()
        {
            Enabled = false;

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
                    "To avoid double-patching loadout behavior, this mod will remain disabled.");
                return;
            }

            var harmony = new Harmony(HarmonyId);

            try
            {
                harmony.PatchAll(typeof(Bootstrap).Assembly);

                if (!AuditPatches())
                {
                    harmony.UnpatchAll(HarmonyId);
                    Log.Error(LogPrefix +
                        "Patch audit failed. All lmaodog patches were removed and the mod is disabled.");
                    return;
                }

                Enabled = true;
                Log.Message(LogPrefix + "Patches installed and audited.");
            }
            catch (Exception e)
            {
                try
                {
                    harmony.UnpatchAll(HarmonyId);
                }
                catch
                {
                    // Preserve the original patching exception below.
                }

                Enabled = false;
                Log.Error(LogPrefix + "Harmony patching failed; the mod is disabled: " + e);
            }
        }

        private static bool AuditPatches()
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
                return false;
            }

            Log.Message(LogPrefix +
                "Patch audit passed (" + found + "/" + targets.Count + ").");
            return true;
        }
    }
}
