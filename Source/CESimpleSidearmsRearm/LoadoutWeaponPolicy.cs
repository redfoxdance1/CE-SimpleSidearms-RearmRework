using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using SimpleSidearms.rimworld;
using Verse;

namespace CESimpleSidearmsRearm
{
    public static class LoadoutWeaponPolicy
    {
        private static readonly HashSet<int> Reconciling = new HashSet<int>();

        public static Dictionary<ThingDef, int> ExactWeaponCounts(Pawn pawn, bool staticSlotsOnly = false)
        {
            var result = new Dictionary<ThingDef, int>();
            Loadout loadout = pawn?.GetLoadout();
            if (loadout == null || loadout.defaultLoadout)
                return result;

            IEnumerable<LoadoutSlot> slots = staticSlotsOnly ? loadout.Slots : loadout.GetSlotsFor(pawn);
            if (slots == null)
                return result;

            foreach (LoadoutSlot slot in slots)
            {
                if (slot?.thingDef == null ||
                    !slot.thingDef.IsWeapon ||
                    slot.count <= 0 ||
                    slot.countType == LoadoutCountType.dropExcess)
                    continue;

                int previous;
                result.TryGetValue(slot.thingDef, out previous);
                result[slot.thingDef] = previous + slot.count;
            }

            return result;
        }

        public static bool CEManages(Pawn pawn, ThingDef def, bool staticSlotsOnly = false)
        {
            return pawn != null &&
                   def != null &&
                   ExactWeaponCounts(pawn, staticSlotsOnly).ContainsKey(def);
        }

        /// <summary>
        /// Simple Sidearms memory is a multiset. For exact weapon defs listed by CE, cap
        /// accidental duplicate memories at CE's requested quantity. Do not create new SS
        /// memories: forgetting a weapon in the SS gizmo remains player intent.
        /// </summary>
        public static void Reconcile(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || pawn.thingIDNumber == 0)
                return;

            if (!Reconciling.Add(pawn.thingIDNumber))
                return;

            try
            {
                CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn, false);
                if (memory?.RememberedWeapons == null)
                    return;

                foreach (KeyValuePair<ThingDef, int> kv in ExactWeaponCounts(pawn))
                {
                    ThingDef def = kv.Key;
                    int wanted = Math.Max(0, kv.Value);

                    while (memory.RememberedWeapons.Count(pair => pair.thing == def) > wanted)
                    {
                        int index = ChooseMemoryRemovalIndex(memory, def);
                        if (index < 0)
                            break;

                        ThingDefStuffDefPair pair = memory.RememberedWeapons[index];
                        memory.ForgetSidearmMemory(pair);
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(
                    Bootstrap.LogPrefix + "Memory reconciliation failed for " +
                    pawn.LabelShortCap + ": " + e,
                    1129464321 ^ pawn.thingIDNumber);
            }
            finally
            {
                Reconciling.Remove(pawn.thingIDNumber);
            }
        }

        private static int ChooseMemoryRemovalIndex(CompSidearmMemory memory, ThingDef def)
        {
            int bestIndex = -1;
            int bestPenalty = int.MaxValue;

            for (int i = memory.RememberedWeapons.Count - 1; i >= 0; i--)
            {
                ThingDefStuffDefPair pair = memory.RememberedWeapons[i];
                if (pair.thing != def)
                    continue;

                int penalty = 0;

                // Preserve explicit player intent whenever there is another duplicate we can
                // remove instead. SS roles are pair-level, not instance-level.
                if (memory.ForcedWeapon.HasValue && memory.ForcedWeapon.Value == pair)
                    penalty += 1000;
                if (memory.ForcedWeaponWhileDrafted.HasValue &&
                    memory.ForcedWeaponWhileDrafted.Value == pair)
                    penalty += 1000;
                if (memory.DefaultRangedWeapon.HasValue && memory.DefaultRangedWeapon.Value == pair)
                    penalty += 100;
                if (memory.PreferredMeleeWeapon.HasValue && memory.PreferredMeleeWeapon.Value == pair)
                    penalty += 100;

                if (penalty < bestPenalty)
                {
                    bestPenalty = penalty;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }
    }

    /// <summary>
    /// CE's GetStorageByThingDef serves both pickup and drop logic. Manual SS sidearms should
    /// be hidden only while CE is calculating excess inventory; hiding them globally changes
    /// loadout satisfaction and generic-slot behavior.
    /// </summary>
    internal static class ExcessScanContext
    {
        [ThreadStatic]
        private static int depth;

        public static bool Active => depth > 0;

        public static void Enter()
        {
            depth++;
        }

        public static void Exit()
        {
            if (depth > 0)
                depth--;
        }
    }

    [HarmonyPatch(
        typeof(JobGiver_RetrieveWeapon),
        nameof(JobGiver_RetrieveWeapon.TryGiveJobStatic),
        new[] { typeof(Pawn), typeof(bool) })]
    public static class RetrieveWeaponReconcilePatch
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn pawn)
        {
            LoadoutWeaponPolicy.Reconcile(pawn);
        }
    }

    [HarmonyPatch(
        typeof(Utility_HoldTracker),
        nameof(Utility_HoldTracker.GetExcessThing),
        new[] { typeof(Pawn), typeof(Thing), typeof(int) },
        new[] { ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out })]
    public static class ExcessThingContextPatch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            ExcessScanContext.Enter();
        }

        [HarmonyFinalizer]
        public static Exception Finalizer(Exception __exception)
        {
            ExcessScanContext.Exit();
            return __exception;
        }
    }

    [HarmonyPatch(
        typeof(Utility_HoldTracker),
        nameof(Utility_HoldTracker.GetStorageByThingDef),
        new[] { typeof(Pawn) })]
    public static class ManualSidearmStorageShieldPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, Dictionary<ThingDef, Integer> __result)
        {
            if (!ExcessScanContext.Active ||
                pawn == null ||
                __result == null ||
                __result.Count == 0)
                return;

            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn, false);
            if (memory?.RememberedWeapons == null || memory.RememberedWeapons.Count == 0)
                return;

            // Static slots only here: ad-hoc GetSlotsFor() can call GetStorageByThingDef(),
            // which would otherwise recurse back through this patch.
            Dictionary<ThingDef, int> managed =
                LoadoutWeaponPolicy.ExactWeaponCounts(pawn, staticSlotsOnly: true);

            foreach (ThingDef def in __result.Keys.Where(d => d != null && d.IsWeapon).ToList())
            {
                if (managed.ContainsKey(def))
                    continue;

                int remembered = memory.RememberedWeapons.Count(pair => pair.thing == def);
                if (remembered <= 0)
                    continue;

                __result[def].value -= remembered;
                if (__result[def].value <= 0)
                    __result.Remove(def);
            }
        }
    }

    [HarmonyPatch(
        typeof(Utility_HoldTracker),
        nameof(Utility_HoldTracker.GetExcessEquipment),
        new[] { typeof(Pawn), typeof(ThingWithComps) },
        new[] { ArgumentType.Normal, ArgumentType.Out })]
    public static class ManualSidearmEquipmentShieldPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ref ThingWithComps dropEquipment, ref bool __result)
        {
            if (!__result || pawn == null || dropEquipment == null)
                return;

            // An exact CE loadout row owns quantity for this def, so do not shield it here.
            if (LoadoutWeaponPolicy.CEManages(pawn, dropEquipment.def, staticSlotsOnly: true))
                return;

            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn, false);
            if (memory?.RememberedWeapons == null)
                return;

            ThingDefStuffDefPair pair =
                new ThingDefStuffDefPair(dropEquipment.def, dropEquipment.Stuff);

            if (memory.RememberedWeapons.Contains(pair))
            {
                dropEquipment = null;
                __result = false;
            }
        }
    }
}
