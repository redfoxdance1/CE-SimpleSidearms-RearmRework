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
                if (Slot?.thingDef != null) return Slot.thingDef.LabelCap;
                if (Slot?.genericDef != null) return Slot.genericDef.label.CapitalizeFirst();
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
            if (loadout == null || loadout.defaultLoadout) return result;

            Dictionary<ThingDef, int> listing = pawn.GetStorageByThingDef()
                .ToDictionary(k => k.Key, v => v.Value.value);

            foreach (LoadoutSlot slot in loadout.GetSlotsFor(pawn)
                .Where(s => s != null && s.countType != LoadoutCountType.dropExcess))
            {
                int wanted = slot.count;

                if (slot.thingDef != null && listing.TryGetValue(slot.thingDef, out int have))
                {
                    int used = Math.Min(wanted, have);
                    wanted -= used;
                    have -= used;
                    if (have <= 0) listing.Remove(slot.thingDef);
                    else listing[slot.thingDef] = have;
                }

                if (wanted > 0 && slot.genericDef != null)
                {
                    foreach (ThingDef def in listing.Keys.Where(d => slot.genericDef.lambda(d)).ToList())
                    {
                        int have = listing[def];
                        int used = Math.Min(wanted, have);
                        wanted -= used;
                        have -= used;
                        if (have <= 0) listing.Remove(def);
                        else listing[def] = have;
                        if (wanted <= 0) break;
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

        private readonly Dictionary<int, RearmState> active = new Dictionary<int, RearmState>();

        public RearmManager(Game game) { }

        public bool IsActive(Pawn pawn) => pawn != null && active.ContainsKey(pawn.thingIDNumber);

        public void Toggle(Pawn pawn)
        {
            if (IsActive(pawn)) Cancel(pawn);
            else Begin(pawn);
        }

        public void Begin(Pawn pawn)
        {
            Loadout loadout = pawn?.GetLoadout();
            if (pawn == null || loadout == null || loadout.defaultLoadout)
            {
                Messages.Message("No Combat Extended loadout is assigned.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            var state = new RearmState { Pawn = pawn, StartTick = Find.TickManager.TicksGame };
            active[pawn.thingIDNumber] = state;
            pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced);
            Advance(state);
        }

        public void Cancel(Pawn pawn)
        {
            if (pawn != null && active.Remove(pawn.thingIDNumber))
                Messages.Message("Rearm cancelled for " + pawn.LabelShortCap + ".", MessageTypeDefOf.NeutralEvent, false);
        }

        public override void GameComponentTick()
        {
            int tick = Find.TickManager.TicksGame;

            if (tick % SyncInterval == 0)
            {
                foreach (Map map in Find.Maps)
                    foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
                        LoadoutWeaponPolicy.Reconcile(pawn);
            }

            if (tick % ProcessInterval != 0 || active.Count == 0) return;

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

                Advance(state);
            }
        }

        private void Advance(RearmState state)
        {
            Pawn pawn = state.Pawn;
            if (pawn == null || !active.ContainsKey(pawn.thingIDNumber)) return;

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
                Log.Error(Bootstrap.LogPrefix + "CE loadout job generation failed: " + e);
                Finish(state, false, "CE loadout job generation failed");
                return;
            }

            if (job == null)
            {
                Finish(state, false, "no obtainable loadout job");
                return;
            }

            if (!excess && deficits.Count > 0 && !IsAcquisitionJob(job))
            {
                Finish(state, false, "required items are unavailable or unreachable");
                return;
            }

            string signature = Signature(job);
            if (signature == state.LastJobSignature) state.SameJobRepeats++;
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
                Log.Error(Bootstrap.LogPrefix + "Could not start rearm job: " + e);
                Finish(state, false, "the next rearm job could not start");
            }
        }

        private static bool IsAcquisitionJob(Job job)
        {
            return job?.def == JobDefOf.Equip ||
                   job?.def == JobDefOf.TakeCountToInventory ||
                   job?.def == CE_JobDefOf.TakeFromOther;
        }

        private static string Signature(Job job)
        {
            string target = job?.targetA.Thing != null
                ? job.targetA.Thing.thingIDNumber.ToString()
                : job?.targetA.Cell.ToString() ?? "none";
            return (job?.def?.defName ?? "null") + ":" + target + ":" + (job?.count ?? 0);
        }

        private void Finish(RearmState state, bool success, string reason)
        {
            Pawn pawn = state.Pawn;
            active.Remove(state.PawnId);
            state.ActiveJob = null;

            if (pawn == null) return;

            if (success)
            {
                Messages.Message(pawn.LabelShortCap + " is rearmed.", MessageTypeDefOf.PositiveEvent, false);
                return;
            }

            List<LoadoutDeficit> deficits = LoadoutEvaluator.GetDeficits(pawn);
            string missing = deficits.Count == 0
                ? "No missing loadout entries remain, but cleanup could not complete."
                : "Still missing: " + string.Join(", ", deficits.Select(d => d.Label + " x" + d.Missing));

            if (!string.IsNullOrEmpty(reason)) missing += " (" + reason + ")";
            Messages.Message("Rearm incomplete for " + pawn.LabelShortCap + ". " + missing,
                MessageTypeDefOf.NegativeEvent, false);
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
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (Gizmo gizmo in __result) yield return gizmo;

            Pawn pawn = __instance;
            if (pawn == null || pawn.Dead || !pawn.IsColonistPlayerControlled ||
                pawn.GetLoadout() == null || pawn.GetLoadout().defaultLoadout || Current.Game == null)
                yield break;

            RearmManager manager = Current.Game.GetComponent<RearmManager>();
            if (manager == null) yield break;

            bool active = manager.IsActive(pawn);
            yield return new Command_Action
            {
                defaultLabel = active ? "Cancel Rearm" : "Rearm",
                defaultDesc = active
                    ? "Cancel the current forced loadout reconciliation."
                    : "Immediately reconcile this pawn with their Combat Extended loadout. Missing or unreachable entries are reported instead of retried forever.",
                action = () => manager.Toggle(pawn)
            };
        }
    }
}
