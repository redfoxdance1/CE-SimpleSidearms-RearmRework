using System;
using System.Collections.Generic;
using System.Linq;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace CESimpleSidearmsRearm
{
    public sealed class LoadoutDeficit
    {
        public LoadoutSlot Slot;
        public int Missing;

        public string Label
        {
            get
            {
                if (Slot?.thingDef != null)
                    return Slot.thingDef.LabelCap;
                if (Slot?.genericDef != null)
                    return Slot.genericDef.label.CapitalizeFirst();
                return "Unknown loadout entry";
            }
        }
    }

    public static class LoadoutEvaluator
    {
        public static List<LoadoutDeficit> GetDeficits(Pawn pawn)
        {
            var result = new List<LoadoutDeficit>();
            Loadout loadout = pawn?.GetLoadout();
            if (loadout == null || loadout.defaultLoadout)
                return result;

            Dictionary<ThingDef, int> listing = pawn.GetStorageByThingDef()
                .ToDictionary(k => k.Key, v => v.Value.value);

            foreach (LoadoutSlot slot in loadout.GetSlotsFor(pawn)
                .Where(s => s != null && s.countType != LoadoutCountType.dropExcess))
            {
                int wanted = slot.count;

                if (slot.thingDef != null &&
                    listing.TryGetValue(slot.thingDef, out int exactHave))
                {
                    int used = Math.Min(wanted, exactHave);
                    wanted -= used;
                    exactHave -= used;

                    if (exactHave <= 0)
                        listing.Remove(slot.thingDef);
                    else
                        listing[slot.thingDef] = exactHave;
                }

                if (wanted > 0 && slot.genericDef != null)
                {
                    foreach (ThingDef def in listing.Keys
                        .Where(d => slot.genericDef.lambda(d)).ToList())
                    {
                        int genericHave = listing[def];
                        int used = Math.Min(wanted, genericHave);
                        wanted -= used;
                        genericHave -= used;

                        if (genericHave <= 0)
                            listing.Remove(def);
                        else
                            listing[def] = genericHave;

                        if (wanted <= 0)
                            break;
                    }
                }

                if (wanted > 0)
                    result.Add(new LoadoutDeficit { Slot = slot, Missing = wanted });
            }

            return result;
        }
    }

    public sealed class RearmManager : GameComponent
    {
        private const int ProcessInterval = 15;
        private const int SyncInterval = 250;
        private const int MaxAttempts = 24;
        private const int MaxDuration = 10000;
        private const int MaxSameJob = 3;
        private const float ForcedSearchRadius = 9999f;

        private readonly Dictionary<int, RearmState> active =
            new Dictionary<int, RearmState>();

        public RearmManager(Game game)
        {
        }

        public bool IsActive(Pawn pawn)
        {
            return Bootstrap.Enabled &&
                   pawn != null &&
                   active.ContainsKey(pawn.thingIDNumber);
        }

        public void Toggle(Pawn pawn)
        {
            if (!Bootstrap.Enabled)
                return;

            if (IsActive(pawn))
                Cancel(pawn);
            else
                Begin(pawn);
        }

        public void Begin(Pawn pawn)
        {
            if (!Bootstrap.Enabled)
                return;

            Loadout loadout = pawn?.GetLoadout();
            if (pawn == null || loadout == null || loadout.defaultLoadout)
            {
                Messages.Message(
                    "No Combat Extended loadout is assigned.",
                    MessageTypeDefOf.RejectInput,
                    false);
                return;
            }

            var state = new RearmState
            {
                Pawn = pawn,
                StartTick = Find.TickManager.TicksGame
            };

            active[pawn.thingIDNumber] = state;
            // Do not interrupt until there is an actual rearm job to start. A pawn whose
            // loadout is already satisfied should keep doing whatever the player assigned.
            Advance(state);
        }

        public void Cancel(Pawn pawn)
        {
            if (pawn == null ||
                !active.TryGetValue(pawn.thingIDNumber, out RearmState state))
                return;

            active.Remove(pawn.thingIDNumber);

            if (state.ActiveJob != null &&
                pawn.jobs != null &&
                pawn.CurJob == state.ActiveJob)
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }

            Messages.Message(
                "Rearm cancelled for " + pawn.LabelShortCap + ".",
                MessageTypeDefOf.NeutralEvent,
                false);
        }

        public override void GameComponentTick()
        {
            if (!Bootstrap.Enabled)
            {
                active.Clear();
                return;
            }

            int tick = Find.TickManager.TicksGame;

            if (tick % SyncInterval == 0)
            {
                foreach (Map map in Find.Maps)
                {
                    foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
                        LoadoutWeaponPolicy.Reconcile(pawn);
                }
            }

            if (tick % ProcessInterval != 0 || active.Count == 0)
                return;

            foreach (RearmState state in active.Values.ToList())
            {
                Pawn pawn = state.Pawn;
                if (pawn == null || pawn.Dead || pawn.Destroyed || pawn.Map == null)
                {
                    active.Remove(state.PawnId);
                    continue;
                }

                if (state.ActiveJob != null && pawn.CurJob == state.ActiveJob)
                    continue;

                // A new player-forced order supersedes Rearm instead of being interrupted by it.
                if (state.ActiveJob != null &&
                    pawn.CurJob != null &&
                    pawn.CurJob != state.ActiveJob &&
                    pawn.CurJob.playerForced)
                {
                    active.Remove(state.PawnId);
                    continue;
                }

                Advance(state);
            }
        }

        private void Advance(RearmState state)
        {
            Pawn pawn = state.Pawn;
            if (!Bootstrap.Enabled ||
                pawn == null ||
                !active.ContainsKey(pawn.thingIDNumber))
                return;

            if (state.Attempts >= MaxAttempts ||
                Find.TickManager.TicksGame - state.StartTick >= MaxDuration)
            {
                Finish(state, false, "retry/time limit reached");
                return;
            }

            LoadoutWeaponPolicy.Reconcile(pawn);

            List<LoadoutDeficit> deficits = LoadoutEvaluator.GetDeficits(pawn);
            bool excess = pawn.HasExcessThing();

            if (deficits.Count == 0 && !excess)
            {
                Finish(state, true, null);
                return;
            }

            Job job;
            try
            {
                job = JobGiver_UpdateLoadout.GetUpdateLoadoutJob(pawn);
            }
            catch (Exception e)
            {
                Log.Error(Bootstrap.LogPrefix +
                          "CE loadout job generation failed: " + e);
                Finish(state, false, "CE loadout job generation failed");
                return;
            }

            // CE performs some drop work synchronously before returning a haul job.
            // Recheck state before treating a null/fallback job as a failure.
            if (job == null || !IsRearmJob(job))
            {
                LoadoutWeaponPolicy.Reconcile(pawn);
                deficits = LoadoutEvaluator.GetDeficits(pawn);
                excess = pawn.HasExcessThing();

                if (deficits.Count == 0 && !excess)
                {
                    Finish(state, true, null);
                    return;
                }

                // Normal CE AI only searches 80 cells for ground items. Rearm is an explicit
                // player command, so if CE otherwise gives up, make one map-wide acquisition
                // attempt using CE's same matching, reservation, danger, food, and capacity rules.
                if (!excess && deficits.Count > 0)
                    job = TryMakeMapWideAcquisitionJob(pawn, deficits);

                if (job == null)
                {
                    Finish(
                        state,
                        false,
                        "required items are unavailable, forbidden, reserved, unreachable, or do not fit");
                    return;
                }
            }

            string signature = Signature(job);
            if (signature == state.LastJobSignature)
            {
                state.SameJobRepeats++;
            }
            else
            {
                state.LastJobSignature = signature;
                state.SameJobRepeats = 1;
            }

            if (state.SameJobRepeats > MaxSameJob)
            {
                Finish(state, false, "the same rearm job failed repeatedly");
                return;
            }

            state.Attempts++;
            state.ActiveJob = job;
            job.playerForced = true;

            try
            {
                pawn.jobs.StartJob(job, JobCondition.InterruptForced);
            }
            catch (Exception e)
            {
                Log.Error(Bootstrap.LogPrefix +
                          "Could not start rearm job " + job + ": " + e);
                Finish(state, false, "the next rearm job could not start");
            }
        }

        private static Job TryMakeMapWideAcquisitionJob(
            Pawn pawn,
            List<LoadoutDeficit> deficits)
        {
            if (pawn?.Map == null)
                return null;

            CompInventory inventory = pawn.TryGetComp<CompInventory>();
            Loadout loadout = pawn.GetLoadout();
            if (inventory == null || inventory.container == null ||
                loadout == null || loadout.defaultLoadout)
                return null;

            foreach (LoadoutDeficit deficit in deficits)
            {
                LoadoutSlot slot = deficit.Slot;
                if (slot == null || deficit.Missing <= 0)
                    continue;

                ThingRequest request;
                Predicate<Thing> matches;

                if (slot.genericDef != null)
                {
                    request = ThingRequest.ForGroup(slot.genericDef.thingRequestGroup);
                    matches = thing =>
                        slot.genericDef.lambda(thing.GetInnerIfMinified().def);
                }
                else if (slot.thingDef != null)
                {
                    request = slot.thingDef.Minifiable
                        ? ThingRequest.ForGroup(ThingRequestGroup.MinifiedThing)
                        : ThingRequest.ForDef(slot.thingDef);

                    matches = thing =>
                        thing.GetInnerIfMinified().def == slot.thingDef;
                }
                else
                {
                    continue;
                }

                Predicate<Thing> search = thing =>
                    matches(thing) &&
                    !thing.IsForbidden(pawn) &&
                    pawn.CanReserve(thing, 10, 1) &&
                    !IsFoodInPrisonForPlayer(thing, pawn) &&
                    EquipmentUtility.CanEquip(thing, pawn) &&
                    AllowedByFoodRestriction(thing, pawn);

                Thing found = GenClosest.ClosestThingReachable(
                    pawn.Position,
                    pawn.Map,
                    request,
                    PathEndMode.ClosestTouch,
                    TraverseParms.For(pawn, Danger.None, TraverseMode.ByPawn),
                    ForcedSearchRadius,
                    search);

                if (found == null)
                    continue;

                if (!inventory.CanFitInInventory(found, out int count))
                    continue;

                count = Math.Min(count, deficit.Missing);

                bool doEquip =
                    found.TryGetComp<CompEquippable>() != null &&
                    !(pawn.story != null && pawn.WorkTagIsDisabled(WorkTags.Violent)) &&
                    pawn.health != null &&
                    pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) &&
                    !pawn.IsItemQuestLocked(pawn.equipment?.Primary) &&
                    (pawn.equipment == null ||
                     pawn.equipment.Primary == null ||
                     !loadout.GetSlotsFor(pawn).Any(s =>
                         s.thingDef == pawn.equipment.Primary.def ||
                         (s.genericDef != null &&
                          s.countType == LoadoutCountType.pickupDrop &&
                          s.genericDef.lambda(pawn.equipment.Primary.def))));

                if (doEquip)
                    count = 1;

                count = Math.Min(GetUnreservedStackCount(found), count);
                if (count <= 0)
                    continue;

                if (doEquip)
                    return JobMaker.MakeJob(JobDefOf.Equip, found);

                Job job = JobMaker.MakeJob(JobDefOf.TakeCountToInventory, found);
                job.count = count;
                job.MakeDriver(pawn);
                return job;
            }

            return null;
        }

        private static bool IsFoodInPrisonForPlayer(Thing thing, Pawn pawn)
        {
            return (thing.GetRoom()?.IsPrisonCell ?? false) &&
                   thing.def.IsNutritionGivingIngestible &&
                   pawn.Faction?.IsPlayer == true;
        }

        private static bool AllowedByFoodRestriction(Thing thing, Pawn pawn)
        {
            if (thing == null || !thing.def.IsNutritionGivingIngestible)
                return true;

            return pawn.foodRestriction
                .GetCurrentRespectedRestriction(pawn)
                ?.Allows(thing) ?? true;
        }

        private static int GetUnreservedStackCount(Thing thing)
        {
            List<ReservationManager.Reservation> reservations =
                thing?.Map?.reservationManager?.ReservationsReadOnly;

            if (reservations == null)
                return 0;

            int stackCount = thing.stackCount;

            for (int i = 0; stackCount > 0 && i < reservations.Count; i++)
            {
                ReservationManager.Reservation reservation = reservations[i];
                if (reservation.Target.Thing == thing)
                    stackCount -= reservation.StackCount;
            }

            return Math.Max(0, stackCount);
        }

        private static bool IsRearmJob(Job job)
        {
            if (job == null)
                return false;

            return job.def == JobDefOf.Equip ||
                   job.def == JobDefOf.TakeCountToInventory ||
                   job.def == CE_JobDefOf.TakeFromOther ||
                   job.def == JobDefOf.HaulToCell ||
                   job.def == JobDefOf.HaulToContainer;
        }

        private static string Signature(Job job)
        {
            string target = job?.targetA.Thing != null
                ? job.targetA.Thing.thingIDNumber.ToString()
                : job?.targetA.Cell.ToString() ?? "none";

            return (job?.def?.defName ?? "null") +
                   ":" + target +
                   ":" + (job?.count ?? 0);
        }

        private void Finish(RearmState state, bool success, string reason)
        {
            Pawn pawn = state.Pawn;
            active.Remove(state.PawnId);
            state.ActiveJob = null;

            if (pawn == null)
                return;

            if (success)
            {
                Messages.Message(
                    pawn.LabelShortCap + " is rearmed.",
                    MessageTypeDefOf.PositiveEvent,
                    false);
                return;
            }

            List<LoadoutDeficit> deficits = LoadoutEvaluator.GetDeficits(pawn);

            string missing = deficits.Count == 0
                ? "No missing loadout entries remain, but cleanup could not complete."
                : "Still missing: " + string.Join(
                    ", ",
                    deficits.Select(d => d.Label + " x" + d.Missing));

            if (!string.IsNullOrEmpty(reason))
                missing += " (" + reason + ")";

            Messages.Message(
                "Rearm incomplete for " + pawn.LabelShortCap + ". " + missing,
                MessageTypeDefOf.NegativeEvent,
                false);
        }

        private sealed class RearmState
        {
            public Pawn Pawn;
            public int StartTick;
            public int Attempts;
            public Job ActiveJob;
            public string LastJobSignature;
            public int SameJobRepeats;

            public int PawnId => Pawn?.thingIDNumber ?? 0;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class RearmGizmoPatch
    {
        [HarmonyPostfix]
        public static IEnumerable<Gizmo> Postfix(
            IEnumerable<Gizmo> __result,
            Pawn __instance)
        {
            foreach (Gizmo gizmo in __result)
                yield return gizmo;

            if (!Bootstrap.Enabled)
                yield break;

            Pawn pawn = __instance;
            if (pawn == null ||
                pawn.Dead ||
                !pawn.IsColonistPlayerControlled ||
                pawn.GetLoadout() == null ||
                pawn.GetLoadout().defaultLoadout ||
                Current.Game == null)
                yield break;

            RearmManager manager = Current.Game.GetComponent<RearmManager>();
            if (manager == null)
                yield break;

            bool isActive = manager.IsActive(pawn);

            yield return new Command_Action
            {
                defaultLabel = isActive ? "Cancel Rearm" : "Rearm",
                defaultDesc = isActive
                    ? "Cancel the current forced loadout reconciliation."
                    : "Immediately reconcile this pawn with their Combat Extended loadout. " +
                      "Missing or unreachable entries are reported instead of retried forever.",
                action = () => manager.Toggle(pawn)
            };
        }
    }
}
