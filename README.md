# CE + Simple Sidearms Rearm

RimWorld 1.6 compatibility/rework patch for **Combat Extended** and **Simple Sidearms**.

## Goal

Make CE loadouts and Simple Sidearms agree about weapon ownership instead of independently fighting over a pawn's inventory.

The primary regression target is the repeated pickup/drop loop where a CE loadout requires one weapon, Simple Sidearms has duplicate memories for that weapon type, SS retrieves another copy, and CE immediately drops the excess copy.

## Authority rules

- **Combat Extended:** authoritative for the quantity of exact weapon defs present in a CE loadout.
- **Simple Sidearms:** authoritative for which carried weapon is currently wielded.
- **This patch:** reconciles the two systems.
- Exact weapon defs listed in CE have their SS memory count capped to CE's requested count.
- SS-only remembered sidearms are protected only during CE's excess/drop calculation; they are not hidden from CE's normal loadout satisfaction logic.
- Generic CE loadout categories remain under CE's native matching behavior.

## Rearm

Selected colonists with a non-default CE loadout receive a **Rearm** command.

Rearm:

1. reconciles CE-managed sidearm memory,
2. asks CE for its next legitimate loadout job,
3. repeats until the loadout is satisfied,
4. stops on unavailable/unreachable requirements or bounded retry limits,
5. reports remaining missing loadout entries once instead of generating endless retries.

A new player-forced order supersedes an active Rearm operation.

## Build

The project targets .NET Framework 4.8 and RimWorld 1.6.

GitHub Actions compiles every push against:

- Combat Extended 1.6.7.3.0 hotfix release assembly
- Simple Sidearms 1.6 assembly
- Krafs RimWorld 1.6 reference assemblies

Warnings are treated as errors and the resulting DLL is uploaded as a workflow artifact.

Local build:

```bash
dotnet build Source/CESimpleSidearmsRearm/CESimpleSidearmsRearm.csproj -c Release
```

By default the project expects Steam Workshop content under:

```text
~/.local/share/Steam/steamapps/workshop/content/294100
```

Override with `-p:RimWorldWorkshopDir=/path/to/294100`.

## Compatibility

Load order:

```text
Harmony
Combat Extended
Simple Sidearms
CE + Simple Sidearms Rearm
```

Do not use this together with another CE + Simple Sidearms compatibility suite that patches the same loadout behavior.

## Verification status

The current source compiles cleanly in CI against the listed CE/SS releases with warnings-as-errors. Runtime behavior still needs an in-game acceptance pass because RimWorld job scheduling, reservations, and Harmony interactions cannot be fully proven by compilation alone.

Primary acceptance case:

```text
CE loadout: AK-style rifle x1
Stockpile: several identical rifles
Expected: pawn obtains/keeps one; no pickup/drop loop
```

Secondary cases include missing weapon, duplicate SS memories, manual SS-only sidearm, generic CE weapon slot, and player interruption during Rearm.

## License

MIT.
