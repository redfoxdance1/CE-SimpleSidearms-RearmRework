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
                    slot.countType == LoadoutCountType.dropExcess)
                    continue;

                int previous;
                result.TryGetValue(slot.thingDef, out previous);
                result[slot.thingDef] = previous + Math.Max(0, slot.count);
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

                    NormalizeRememberedPairsToCarriedCopies(pawn, memory, def);
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

        /// <summary>
        /// CE exact rows care about ThingDef, while SS memory also keys on Stuff. If CE is
        /// already satisfied by a carried copy with different Stuff, keep the existing memory
        /// count but rebind stale pair entries to carried pairs. This prevents SS from fetching
        /// an extra material variant that CE would immediately discard. A pawn with zero SS
        /// memories stays at zero, preserving explicit "forget sidearm" intent.
        /// </summary>
        private static void NormalizeRememberedPairsToCarriedCopies(
            Pawn pawn,
            CompSidearmMemory memory,
            ThingDef def)
        {
            List<int> memoryIndices = new List<int>();
            for (int i = 0; i < memory.RememberedWeapons.Count; i++)
            {
                if (memory.RememberedWeapons[i].thing == def)
                    memoryIndices.Add(i);
            }

            if (memoryIndices.Count == 0)
                return;

            List<ThingDefStuffDefPair> available = CarriedPairs(pawn, def);
            if (available.Count == 0)
                return;

            var unmatched = new List<int>();

            foreach (int index in memoryIndices)
            {
                ThingDefStuffDefPair remembered = memory.RememberedWeapons[index];
                int carriedIndex = available.FindIndex(pair => pair == remembered);

                if (carriedIndex >= 0)
                    available.RemoveAt(carriedIndex);
                else
                    unmatched.Add(index);
            }

            foreach (int index in unmatched)
            {
                if (available.Count == 0)
                    break;

                ThingDefStuffDefPair oldPair = memory.RememberedWeapons[index];
                ThingDefStuffDefPair newPair = available[0];
                available.RemoveAt(0);

                if (oldPair == newPair)
                    continue;

                memory.rememberedWeapons[index] = newPair;

                // SS roles are pair-level. Only move a role when the old pair no longer has
                // another remembered copy backing it.
                if (!memory.RememberedWeapons.Contains(oldPair))
                    RebindRoles(memory, oldPair, newPair);
            }
        }

        private static List<ThingDefStuffDefPair> CarriedPairs(Pawn pawn, ThingDef def)
        {
            var result = new List<ThingDefStuffDefPair>();

            if (pawn?.equipment?.Primary != null &&
                pawn.equipment.Primary.def == def)
            {
                result.Add(new ThingDefStuffDefPair(
                    pawn.equipment.Primary.def,
                    pawn.equipment.Primary.Stuff));
            }

            if (pawn?.inventory?.innerContainer != null)
            {
                foreach (Thing thing in pawn.inventory.innerContainer)
                {
                    if (!(thing is ThingWithComps weapon) ||
                        weapon.def != def ||
                        !weapon.def.IsWeapon)
                        continue;

                    var pair = new ThingDefStuffDefPair(weapon.def, weapon.Stuff);
                    int copies = Math.Max(1, weapon.stackCount);
                    for (int i = 0; i < copies; i++)
                        result.Add(pair);
                }
            }

            return result;
        }

        private static void RebindRoles(
            CompSidearmMemory memory,
            ThingDefStuffDefPair oldPair,
            ThingDefStuffDefPair newPair)
        {
            if (memory.ForcedWeapon.HasValue && memory.ForcedWeapon.Value == oldPair)
                memory.ForcedWeapon = newPair;

            if (memory.ForcedWeaponWhileDrafted.HasValue &&
                memory.ForcedWeaponWhileDrafted.Value == oldPair)
                memory.ForcedWeaponWhileDrafted = newPair;

            if (memory.DefaultRangedWeapon.HasValue &&
                memory.DefaultRangedWeapon.Value == oldPair)
                memory.DefaultRangedWeapon = newPair;

            if (memory.PreferredMeleeWeapon.HasValue &&
                memory.PreferredMeleeWeapon.Value == oldPair)
                memory.PreferredMeleeWeapon = newPair;
        }
    }

    internal static class ManualSidearmProtection
    {
        /// <summary>
        /// Assign SS memory copies to concrete carried instances. The equipped primary consumes
        /// memory first because CE drops equipment separately; remaining memory protects matching
        /// def+stuff copies in inventory. This avoids a remembered material variant shielding a
        /// different physical copy that merely shares the same ThingDef.
        /// </summary>
        public static Dictionary<Thing, int> ProtectedInventoryStacks(
            Pawn pawn,
            CompSidearmMemory memory)
        {
            var protectedStacks = new Dictionary<Thing, int>();

            if (pawn?.inventory?.innerContainer == null ||
                memory?.RememberedWeapons == null ||
                memory.RememberedWeapons.Count == 0)
                return protectedStacks;

            Dictionary<ThingDefStuffDefPair, int> remaining = memory.RememberedWeapons
                .Where(pair => pair.thing != null)
                .GroupBy(pair => pair)
                .ToDictionary(group => group.Key, group => group.Count());

            if (pawn.equipment?.Primary != null)
            {
                var primaryPair = new ThingDefStuffDefPair(
                    pawn.equipment.Primary.def,
                    pawn.equipment.Primary.Stuff);

                if (remaining.TryGetValue(primaryPair, out int primaryMemories) &&
                    primaryMemories > 0)
                {
                    if (primaryMemories == 1)
                        remaining.Remove(primaryPair);
                    else
                        remaining[primaryPair] = primaryMemories - 1;
                }
            }

            foreach (Thing thing in pawn.inventory.innerContainer)
            {
                if (!(thing is ThingWithComps weapon) || !weapon.def.IsWeapon)
                    continue;

                var pair = new ThingDefStuffDefPair(weapon.def, weapon.Stuff);
                if (!remaining.TryGetValue(pair, out int memoriesLeft) || memoriesLeft <= 0)
                    continue;

                int protectCount = Math.Min(thing.stackCount, memoriesLeft);
                if (protectCount <= 0)
                    continue;

                protectedStacks[thing] = protectCount;

                memoriesLeft -= protectCount;
                if (memoriesLeft <= 0)
                    remaining.Remove(pair);
                else
                    remaining[pair] = memoriesLeft;
            }

            return protectedStacks;
        }

        public static Dictionary<ThingDef, int> ProtectedStorageCountsByDef(
            Pawn pawn,
            CompSidearmMemory memory)
        {
            var result = new Dictionary<ThingDef, int>();

            // CE's storage dictionary includes the equipped primary. GetExcessEquipment handles
            // that object separately, so a remembered manual primary must also be removed from
            // the nested inventory-excess view or its def can block scanning later excess items.
            if (pawn?.equipment?.Primary != null && memory?.RememberedWeapons != null)
            {
                var primaryPair = new ThingDefStuffDefPair(
                    pawn.equipment.Primary.def,
                    pawn.equipment.Primary.Stuff);

                if (memory.RememberedWeapons.Contains(primaryPair))
                    result[pawn.equipment.Primary.def] = 1;
            }

            foreach (KeyValuePair<Thing, int> entry in ProtectedInventoryStacks(pawn, memory))
            {
                int current;
                result.TryGetValue(entry.Key.def, out current);
                result[entry.Key.def] = current + entry.Value;
            }

            return result;
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

        [HarmonyPostfix]
        public static void Postfix(
            Pawn pawn,
            ref Thing dropThing,
            ref int dropCount,
            ref bool __result)
        {
            if (!__result ||
                pawn == null ||
                dropThing == null ||
                !dropThing.def.IsWeapon ||
                LoadoutWeaponPolicy.CEManages(pawn, dropThing.def, staticSlotsOnly: true))
                return;

            CompSidearmMemory memory = CompSidearmMemory.GetMemoryCompForPawn(pawn, false);
            if (memory?.RememberedWeapons == null)
                return;

            Dictionary<Thing, int> protectedStacks =
                ManualSidearmProtection.ProtectedInventoryStacks(pawn, memory);

            if (!protectedStacks.TryGetValue(dropThing, out int protectedFromCandidate) ||
                protectedFromCandidate <= 0)
                return;

            int unprotectedInCandidate = dropThing.stackCount - protectedFromCandidate;
            if (unprotectedInCandidate > 0)
            {
                dropCount = Math.Min(dropCount, unprotectedInCandidate);
                return;
            }

            // CE's count scan is def-level and its final selection is FirstOrDefault. If that
            // happens to point at the protected copy, redirect to an unprotected copy of the
            // same def rather than throwing away the sidearm SS owns.
            foreach (Thing candidate in pawn.inventory.innerContainer)
            {
                if (candidate == dropThing ||
                    candidate.def != dropThing.def ||
                    pawn.IsItemQuestLocked(candidate))
                    continue;

                int protectedCount = 0;
                protectedStacks.TryGetValue(candidate, out protectedCount);

                int unprotectedCount = candidate.stackCount - protectedCount;
                if (unprotectedCount <= 0)
                    continue;

                dropThing = candidate;
                dropCount = Math.Min(dropCount, unprotectedCount);
                return;
            }

            // The only physical inventory copy CE selected is protected. Its remaining def-level
            // excess can only come from a separately handled equipped copy, so do not sacrifice
            // the remembered inventory sidearm here.
            dropThing = null;
            dropCount = 0;
            __result = false;
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

            Dictionary<ThingDef, int> protectedStorage =
                ManualSidearmProtection.ProtectedStorageCountsByDef(pawn, memory);

            foreach (KeyValuePair<ThingDef, int> entry in protectedStorage)
            {
                ThingDef def = entry.Key;

                if (managed.ContainsKey(def) ||
                    !__result.TryGetValue(def, out Integer stored))
                    continue;

                stored.value -= entry.Value;
                if (stored.value <= 0)
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
