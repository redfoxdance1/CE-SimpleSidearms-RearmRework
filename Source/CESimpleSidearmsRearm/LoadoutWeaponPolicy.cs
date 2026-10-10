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
            if (slots == null) return result;

            foreach (LoadoutSlot slot in slots)
            {
                if (slot?.thingDef == null || !slot.thingDef.IsWeapon || slot.count <= 0 ||
                    slot.countType == LoadoutCountType.dropExcess)
                    continue;

                int old;
                result.TryGetValue(slot.thingDef, out old);
                result[slot.thingDef] = old + slot.count;
            }
            return result;
        }

        public static bool CEManages(Pawn pawn, ThingDef def, bool staticSlotsOnly = false)
        {
            return pawn != null && def != null && ExactWeaponCounts(pawn, staticSlotsOnly).ContainsKey(def);
        }

        public static void Reconcile(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || pawn.thingIDNumber == 0)
                return;
            if (!Reconciling.Add(pawn.thingIDNumber))
                return;

            try
            {
                CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn, false);
                if (memory?.RememberedWeapons == null) return;

                foreach (var kv in ExactWeaponCounts(pawn))
                {
                    ThingDef def = kv.Key;
                    int wanted = Math.Max(0, kv.Value);

                    while (memory.RememberedWeapons.Count(p => p.thing == def) > wanted)
                    {
                        int index = LastIndexOfDef(memory.RememberedWeapons, def);
                        if (index < 0) break;
                        memory.ForgetSidearmMemory(memory.RememberedWeapons[index]);
                    }

                    int remembered = memory.RememberedWeapons.Count(p => p.thing == def);
                    if (remembered >= wanted) continue;

                    foreach (ThingWithComps weapon in CarriedWeapons(pawn, def))
                    {
                        if (remembered >= wanted) break;
                        ThingDefStuffDefPair pair = new ThingDefStuffDefPair(weapon.def, weapon.Stuff);

                        int pairMem = memory.RememberedWeapons.Count(p => p == pair);
                        int pairCarried = CarriedWeapons(pawn, def)
                            .Count(w => new ThingDefStuffDefPair(w.def, w.Stuff) == pair);

                        if (pairMem >= pairCarried) continue;
                        memory.rememberedWeapons.Add(pair);
                        remembered++;
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(Bootstrap.LogPrefix + "Memory reconciliation failed for " + pawn.LabelShortCap + ": " + e, 1129464321);
            }
            finally
            {
                Reconciling.Remove(pawn.thingIDNumber);
            }
        }

        private static int LastIndexOfDef(List<ThingDefStuffDefPair> list, ThingDef def)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].thing == def) return i;
            return -1;
        }

        private static List<ThingWithComps> CarriedWeapons(Pawn pawn, ThingDef def)
        {
            var result = new List<ThingWithComps>();
            if (pawn?.equipment?.Primary != null && pawn.equipment.Primary.def == def)
                result.Add(pawn.equipment.Primary);

            if (pawn?.inventory?.innerContainer != null)
                foreach (Thing thing in pawn.inventory.innerContainer)
                    if (thing is ThingWithComps weapon && weapon.def == def && weapon.def.IsWeapon)
                        result.Add(weapon);

            return result;
        }
    }

    [HarmonyPatch(typeof(JobGiver_RetrieveWeapon), nameof(JobGiver_RetrieveWeapon.TryGiveJobStatic))]
    public static class RetrieveWeaponReconcilePatch
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn pawn) => LoadoutWeaponPolicy.Reconcile(pawn);
    }

    [HarmonyPatch(typeof(Utility_HoldTracker), nameof(Utility_HoldTracker.GetStorageByThingDef), new[] { typeof(Pawn) })]
    public static class ManualSidearmStorageShieldPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, Dictionary<ThingDef, Integer> __result)
        {
            if (pawn == null || __result == null || __result.Count == 0) return;

            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn, false);
            if (memory?.RememberedWeapons == null) return;

            // Static slots only: CE ad-hoc GetSlotsFor() may itself call GetStorageByThingDef().
            var managed = LoadoutWeaponPolicy.ExactWeaponCounts(pawn, true);

            foreach (ThingDef def in __result.Keys.Where(d => d != null && d.IsWeapon).ToList())
            {
                if (managed.ContainsKey(def)) continue;
                int remembered = memory.RememberedWeapons.Count(p => p.thing == def);
                if (remembered <= 0) continue;

                __result[def].value -= remembered;
                if (__result[def].value <= 0)
                    __result.Remove(def);
            }
        }
    }

    [HarmonyPatch(typeof(Utility_HoldTracker), nameof(Utility_HoldTracker.GetExcessEquipment),
        new[] { typeof(Pawn), typeof(ThingWithComps) },
        new[] { ArgumentType.Normal, ArgumentType.Out })]
    public static class ManualSidearmEquipmentShieldPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ref ThingWithComps dropEquipment, ref bool __result)
        {
            if (!__result || pawn == null || dropEquipment == null) return;
            if (LoadoutWeaponPolicy.CEManages(pawn, dropEquipment.def, true)) return;

            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn, false);
            if (memory?.RememberedWeapons == null) return;

            var pair = new ThingDefStuffDefPair(dropEquipment.def, dropEquipment.Stuff);
            if (memory.RememberedWeapons.Contains(pair))
            {
                dropEquipment = null;
                __result = false;
            }
        }
    }
}
