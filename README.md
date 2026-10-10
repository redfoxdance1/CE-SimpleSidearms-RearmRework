# CE + Simple Sidearms Rearm

RimWorld 1.6 compatibility/rework patch for **Combat Extended** and **Simple Sidearms**.

## Goals

This project makes CE loadouts and Simple Sidearms agree about weapon ownership instead of independently fighting over a pawn's inventory.

The first target is the repeated pickup/drop loop caused when Simple Sidearms remembers more copies of a weapon type than the CE loadout actually requires.

This patch treats **Combat Extended as authoritative for the quantity of exact weapon defs that appear in a CE loadout**. Simple Sidearms remains authoritative for which carried weapon is currently wielded and can continue remembering manual sidearms whose defs are not in the CE loadout.

## Planned / prototype behavior

- Reconcile duplicate Simple Sidearms memory for exact weapon defs declared by the pawn's CE loadout.
- Protect manually remembered sidearms whose defs are not managed by the CE loadout.
- Add a **Rearm** pawn command.
- Rearm forcibly runs CE loadout jobs until the loadout is satisfied, cancelled, or a bounded retry limit is reached.
- Rearm reports remaining missing loadout entries instead of retrying forever.

## Authority rules

- **CE loadout:** what equipment and how much should be carried.
- **Simple Sidearms:** which carried weapon should be wielded.
- **This patch:** synchronization and arbitration between them.
- If an exact weapon def exists in the CE loadout, its CE count wins.
- A Simple Sidearms weapon def that does not exist in the CE loadout is treated as a manual sidearm.

## Status

Prototype source for RimWorld 1.6. Not yet verified in-game.

## License

MIT.
