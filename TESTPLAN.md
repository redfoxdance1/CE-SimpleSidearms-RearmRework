# lmaodog acceptance test plan

Compilation proves API compatibility, but these cases exercise RimWorld job scheduling, reservations,
Simple Sidearms memory, and Combat Extended loadout behavior in-game.

Run with only:

1. Harmony
2. Combat Extended
3. Simple Sidearms
4. CE + Simple Sidearms Rearm (lmaodog)

Open the dev log before each case. On startup the expected messages are:

```text
[CE+SS Rearm] Patch audit passed (5/5).
[CE+SS Rearm] Patches installed and audited.
```

Any red error from lmaodog invalidates the run.

## A. Original pickup/drop regression

Arrange:

- One player colonist.
- Non-default CE loadout with one exact rifle def, count 1.
- Five identical copies of that rifle in reachable storage.
- Simple Sidearms re-equip enabled.

Act:

- Leave the pawn idle long enough for normal CE loadout enforcement.
- Then click Rearm once.

Pass:

- Pawn ends with exactly one copy total across equipped + inventory.
- SS memory contains no more than one entry for that exact weapon def.
- No repeated pickup -> drop -> pickup cycle occurs.
- Rearm terminates.

## B. Duplicate-memory repair

Arrange:

- Same CE exact weapon row, count 1.
- Use SS actions/equipping to create two remembered entries of the same weapon type if possible.
- Pawn may carry one or two physical copies.

Act:

- Allow 250+ game ticks or trigger SS retrieval.

Pass:

- SS remembered count for the CE-managed weapon falls to 1.
- A forced/default/preferred pair is retained in preference to an unprotected duplicate.
- CE is allowed to remove any physical copy above its count of 1.

## C. Replacement after loss

Arrange:

- CE exact weapon row, count 1.
- Pawn has one remembered/carried copy.
- At least one replacement copy exists in storage.

Act:

- Destroy/drop the carried weapon.
- Click Rearm.

Pass:

- Pawn obtains one replacement.
- Rearm ends successfully.
- No extra duplicate is retrieved afterward.

## D. Missing equipment

Arrange:

- CE exact weapon row, count 1.
- No matching item anywhere on the map or eligible pack/prisoner inventory.

Act:

- Click Rearm.

Pass:

- Rearm terminates instead of looping.
- One final message reports the weapon as still missing.
- No repeated retrieval jobs are generated.

## E. Full-map retrieval

Arrange:

- Large map.
- Pawn and required weapon more than 80 cells apart.
- Weapon is reachable, allowed, reservable, equippable, and fits CE mass/bulk.

Act:

- Click Rearm.

Pass:

- Pawn obtains it despite CE's ordinary autonomous 80-cell ground search limit.

## F. Forbidden / reserved / unreachable / capacity failures

Repeat the missing-equipment test with the only required copy:

- forbidden,
- reserved by another pawn/job,
- behind an unreachable path,
- too heavy/bulky for CE inventory capacity.

Pass:

- Rearm terminates with one incomplete notification.
- It does not churn the same job indefinitely.

## G. Manual SS-only sidearm

Arrange:

- CE loadout does not list a pistol.
- Remember one pistol manually in SS.
- Set CE loadout to drop undefined items.

Act:

- Allow CE loadout enforcement to run.

Pass:

- The remembered pistol is not dropped.
- An additional unremembered copy of the same def may be dropped.
- If variants with different Stuff exist, remembering one variant does not protect a different variant.

## H. Equipped manual sidearm

Arrange:

- Equip an SS-remembered weapon whose def is not an exact CE loadout row.
- CE drop-undefined enabled.

Pass:

- CE does not strip the remembered equipped weapon.

## I. CE quantity wins

Arrange:

- CE exact weapon row count 1.
- SS has remembered two copies of that exact def.

Pass:

- Memory is capped to 1.
- CE may drop physical copies above 1.
- Manual SS memory cannot force quantity above the CE exact row.

Repeat with CE count 2 and confirm two remembered copies are allowed.

## J. Generic CE slot

Arrange:

- A CE generic weapon slot rather than an exact weapon def.
- One SS-remembered sidearm and additional candidate weapons.

Pass:

- No recursion or log errors.
- Normal CE acquisition still sees actual carried inventory.
- Manual remembered sidearm protection applies only to CE's excess/drop scan.

## K. Player interruption

Arrange:

- Start Rearm while the pawn must walk to equipment.

Act:

- Before arrival, issue a different player-forced order.

Pass:

- The new order wins.
- Rearm stops and does not interrupt the new forced job on its next poll.

## L. Conflicting compatibility suite

Arrange:

- Enable eebette's CE + Simple Sidearms compatibility patch alongside lmaodog.

Pass:

- lmaodog logs that an overlapping suite is active.
- lmaodog remains disabled.
- Rearm gizmo is absent.
- Its periodic reconciliation does not run.

## M. Save/load smoke test

Arrange:

- Stable pawn with CE loadout and SS memories.
- Save and reload.

Pass:

- No Harmony errors.
- Startup patch audit reports 5/5.
- Normal CE/SS behavior resumes.
- An in-progress Rearm does not need to survive save/load; it may be started again manually.

## Minimum release gate

Do not call the build release-ready until A, C, D, E, G, I, K, and M pass with no unexpected
red errors or repeated warning spam.
