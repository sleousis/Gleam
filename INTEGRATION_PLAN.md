# Gleam — Inventory Organizer: integration plan

Status: Phases 1–3 complete (2026-09-08). Steps 1–13 of §2.7 are committed; step 14's in-game verification is listed below.

**In-game verification checklist (not yet run):**
- Troubleshooting → "To saddlebag and back" on a junk stack: confirms `MoveItemSlot` plus the slot-watch confirmation.
- Troubleshooting → "Live sizes" with the saddlebag and a retainer open: confirms page sizes (and premium saddlebag pages).
- Organizer → Preview with the starter layout: cards, shortfall banner when a rule overfills something, move groups.
- Organize here only with the saddlebag open, then a retainer open: confirms landing-slot choice and merges.
- Hands-free organise with at least one retainer-to-retainer rule: confirms relays through the bags and the second leg finding its stack by identity.
- Armoury destination: a piece of gear from a retainer into the armoury should land on its own slot page.
Constraint honoured throughout: the discard feature keeps its behaviour; the organizer is additive.

---

## Part 1 — What exists today (Phase 1 findings)

### 1.1 Enumeration and item identity

| Concern | Where | Notes |
|---|---|---|
| Live containers | `src/Gleam/Game/GameInventoryScanner.cs` | Reads `InventoryManager` natively, page by page (`GameContainerIds.*Pages`). Reports "loaded" per container. Retainer pages are only considered open once `RetainerPage1.IsLoaded` and the active retainer id matches. Dresser via `MirageManager`. Framework thread only. |
| Closed containers | `src/Gleam/Integrations/AllaganToolsSource.cs` → `Core/Integrations/OfflineInventory.cs` | Allagan Tools IPC (`GetCharacterItems` as `ulong[]` records). `AllaganItemRecord.Parse` decodes container/slot/item/qty/flags/materia/stains/retainerId. |
| Folding live + cached | `RunCoordinator.OfflineItems` | Live always wins per (kind, owner). Retainers appear as their own cache entries; folded into the owner's plan. |
| Item identity | `Core/Model/ScannedItem.cs`, `SlotRef.cs` | `SlotRef(Kind, ContainerId, Slot, OwnerId)`; `ScannedItem(ItemId base, Quantity, IsHq, IsCollectable, Materia[], Stain0/1, Spiritbond)`. Materia is only read for equipment (stackables reuse those bytes). |
| Static item facts | `Game/ItemDatabase.cs` → `Core/Model/ItemInfo.cs` | `UiCategory`, `LevelEquip`, `ItemLevel`, `ClassJobCategoryId`, `IsEquipment`, `IsUnique`, `IsUntradable`, `StackSize`, `IsMarketable`, `IsUsable`. Cached per id. |
| Container id tables | `Core/Model/ContainerKind.cs :: GameContainerIds` | Raw `InventoryType` values mirrored into Core so Core never references game assemblies. |

**Important quirk already handled:** cached retainer slot numbers follow the on-screen 5×35 tab layout, not the 7×25 memory layout. The executor therefore never trusts a cached position; it relocates by identity (`IGameActions.FindSlot`) before acting. The organizer inherits this: cached rows are *inputs to planning*, never *addresses for execution*.

### 1.2 Rule / selection engine

- `Core/Rules/IRule` + `RuleEngine`: rules look at one stack and return a `Proposal` or null; most confident wins.
- `Core/Planning/RunPlanner`: hard blocks (`HardBlocks`) → protect list → always-clean list → rules → market annotation → `ActionPolicyApplier` (preset) → `ContainerConstraints` → `PlanRow` (checked/unchecked, chosen action).
- `Core/Rules/Thresholds` + `Presets`: numeric knobs and the three presets.

This engine answers *"should this leave the game?"*. It is destructive-by-design (every `ActionKind` is a disposal). It is **not** reusable as the organizer's rule engine, but its **predicate vocabulary** is: category, equip level, item level, job category (`ItemContext.MaxLevelForCategory`), HQ, materia, unique/untradeable. The organizer needs a *separate* rule type with a *destination* rather than an *action*.

### 1.3 Execution loop

| Piece | Where | Reusable? |
|---|---|---|
| Queue model | `Core/Execution/QueuedAction.cs` | Frozen contract row; `Followup` for two-leg actions (used for "bring home then act"). **Pattern reusable**; type is disposal-specific (has `Action: ActionKind`). |
| Engine | `Core/Execution/ExecutionEngine.cs` | Orders by container, checks container/NPC availability, re-validates each slot by identity, rate-limits (`ExecutionOptions.RateLimit`, default 250 ms), parks pending items with reasons, aborts after `MaxConsecutiveFailures` (3). **The loop skeleton is exactly what the organizer needs**; the per-action body is disposal-specific. |
| Game adapter | `Game/GameActions.cs` | `RunAndAwaitRemoval`: arm dialog → native call → wait `InventoryItemRemoved/Changed` event → fall back to two live re-reads 2 s apart in a *loaded* container. `MoveToInventoryAsync` (retainer → bags via "Retrieve from Retainer" context entry, then `FindSlot` to learn the landing slot). `MarketListAsync` polls the slot instead of waiting for events because a listing is reported as a *move*. |
| Native move | `Game/StackMerger.cs` | **Already calls `InventoryManager.MoveItemSlot(fromType, fromSlot, toType, toSlot, true)`** for stack merges (same kind, same owner). Returns non-zero on acceptance; no wait for confirmation, fixed pause. |
| Merge planning | `Core/Merging/StackMergePlanner.cs` | Pure planner: groups (kind, owner, item, HQ), fills fullest stacks first, `SlotsFreed`. Runs before every scan when `Profile.StackMergeBeforeScan`. |
| Session context | `Automation/AutoPilot.cs` | Legs: bags → saddlebag (`MainCommand`) → inn (Lifestream) → each retainer at the bell (`RetainerList` select, `SelectString` menu, inventory window, market sell list) → dresser → GC → merchant. `RecoverUiAsync` closes leftover windows. Per-retainer free market slots read from `RetainerManager`. |
| Dialog handling | `Game/AddonDriver.cs`, `Game/InventoryContextDriver.cs` | Arm-before-act `SelectYesno` with normalized item-name match; context menu driven by Addon row id (language-neutral). |
| History | `Core/Logging/RunLog.cs` | JSON-lines append of every Done action. Schema carries `Action: ActionKind`. |

### 1.4 UI

- `Windows/ConfirmationWindow.cs`: header, preset switch, container and type chips, per-section tables (sortable headers, sticky titles), row = checkbox · icon · name · action dropdown · market price · attribute pills, footer with summary and primary button. Driven by `RunCoordinator.CurrentPlan` (`RunPlan` → `PlanSection` → `PlanRow`).
- `Windows/Ui.cs`: shared style primitives (chips, pills, cards, header, banner, segmented, key/value rows, rich tooltip).
- `Windows/SettingsWindow*.cs`: cards + "More" folds; `ListEditor` for the two item lists.

### 1.5 Gaps for an organizer

1. **Moving is only implemented for same-container stack merges.** No cross-container move, no capacity check, no confirmation wait, no relocation-by-identity for the *destination*.
2. **No capacity model.** Nothing knows a container's size or free slot count except `FreeInventorySlots()` (bags) and `FreeMarketSlots()`.
3. **No split.** `MoveItemSlot` moves whole stacks or merges onto a same-item stack; splitting a stack needs `InventoryManager.SplitItem`-style calls (to verify) or a numeric dialog.
4. **Relay constraint.** The game only moves between the player's bags/armoury and *one* open storage (a retainer's pages, or the saddlebag). Retainer↔retainer and retainer↔saddlebag must relay through the bags with finite staging space. Nothing models this.
5. **Session model is action-shaped.** `AutoPilot` legs are keyed to disposal action kinds; the organizer needs legs keyed to *containers that must be open*.
6. **Armoury targets.** Armoury sub-containers are per equip slot; a move into "armoury" must pick the right page from the item's `EquipSlotCategory`. Not modelled in `ItemInfo` yet (only `IsEquipment`).

### 1.6 Architectural friction

- `QueuedAction.Action`, `ActionResult`, `RunLogEntry` and `HistoryWindow` all assume a disposal `ActionKind`. Adding `ActionKind.Move` would leak organizer concepts into the discard planner (presets, `ContainerConstraints`, `ActionPolicyApplier`) and violate "don't touch discard". → Use a **separate queue/result type** for moves and a small **shared executor core** extracted from `ExecutionEngine` (see 2.5). The discard engine keeps its public surface.
- `RunCoordinator` owns scan → plan → execute for disposal and holds the single `CurrentPlan`. The organizer needs the same *scan* but a different *plan*. → Extract the scan (live + offline fold + context) into a reusable `InventorySnapshotService`; both coordinators consume it.
- `ConfirmationWindow` is bound to `RunPlan`. Its **primitives** (`Ui.*`, section/table layout, chips) are reusable; the window itself is not. → New `OrganizerWindow` built from the same primitives.
- `StackMergePlanner` and `StackMerger` are the seed of the mover, but `StackMerger` has no confirmation wait. → Promote to a confirmed `MoveExecutor` and have the merge pass use it (behaviour-preserving refactor; the only touch to existing code paths, and it makes merges *safer*, not different).

---

## Part 2 — Plan

### 2.1 Module layout

```
src/Gleam.Core/
  Organizer/
    Model/
      Destination.cs          # Bags | Armoury | Saddlebag | Retainer(id) | KeepWherever
      OrganizerRule.cs        # ordered predicates → destination
      OrganizerPlan.cs        # named profile: rules + options (persisted)
      Predicate.cs            # category / ilvl / equip level / job / HQ / materia / stackable / tag / item-list
    Capacity/
      ContainerCapacity.cs    # size, used, free, stack headroom per (kind, owner, page)
    Solving/
      DesiredState.cs         # item → destination after first-match
      MoveSolver.cs           # diff → MoveOp list, feasibility, ordering, relay legs, multi-pass
      Feasibility.cs          # per-container needs/free report
    Execution/
      MoveOp.cs               # From SlotRef, To Destination(+page hint), ItemId, Qty, HQ, Leg(Direct|RelayOut|RelayIn)
      MoveBatch.cs            # ops grouped by required SessionContext
      IMoveActions.cs         # game surface for moves (see 2.5)
      MoveExecutor.cs         # reuses ExecutionCore
  Execution/
    ExecutionCore.cs          # NEW: extracted loop (ordering hook, availability hook, re-validate, rate limit, park, abort)
    ExecutionEngine.cs        # UNCHANGED public surface; delegates to ExecutionCore
  Merging/
    StackMergePlanner.cs      # unchanged

src/Gleam/
  Services/
    InventorySnapshotService.cs   # NEW: extracted from RunCoordinator (scan + offline fold + context)
    OrganizerCoordinator.cs       # NEW: snapshot → solve → preview → execute; pending/resume
  Game/
    MoveActions.cs            # NEW: IMoveActions over InventoryManager.MoveItemSlot + confirmation wait
    StackMerger.cs            # thin wrapper over MoveActions (behaviour preserved)
  Automation/
    AutoPilot.Organizer.cs    # NEW partial: container-keyed legs (open saddlebag / summon retainer N)
  Windows/
    OrganizerWindow.cs        # rules editor + preview + run
    OrganizerRuleEditor.cs
tests/Gleam.Core.Tests/
    OrganizerSolverTests.cs, OrganizerRuleTests.cs, MoveExecutorTests.cs
```

Everything in `Core/Organizer` is pure and unit-testable; game access stays in `src/Gleam/Game`.

### 2.2 Rule / plan data model

```csharp
public enum DestinationKind { Bags, Armoury, Saddlebag, Retainer, Stay }
public readonly record struct Destination(DestinationKind Kind, ulong RetainerId = 0);

public sealed class OrganizerPredicate
{
    // All set fields must match (AND). A rule with no fields matches everything (a catch-all).
    public HashSet<ItemTag>? Tags;              // Gear, Materia, Materials, Consumables, Crystals, Housing, Collectibles, Other
    public HashSet<string>? UiCategories;       // exact game categories, e.g. "Medicine"
    public int? MinItemLevel, MaxItemLevel;     // gear only
    public int? MinEquipLevel, MaxEquipLevel;
    public HashSet<uint>? ClassJobCategoryIds;  // "gear for jobs I play" via ItemContext, or explicit
    public bool? IsHq, HasMateria, IsStackable, IsUntradable, IsUnique;
    public bool? OnProtectList;                 // reuse ItemList
    public HashSet<uint>? ItemIds;              // explicit picks
}

public sealed class OrganizerRule
{
    public Guid Id; public string Name; public bool Enabled = true;
    public OrganizerPredicate When;
    public Destination Then;
}

public sealed class OrganizerPlan       // a named profile
{
    public Guid Id; public string Name;
    public List<OrganizerRule> Rules;   // ordered, FIRST MATCH WINS
    public Destination Fallback = Destination.Stay;
    public bool MergeStacksAtDestination = true;
    public HashSet<ulong> RetainersInScope;   // empty = all known
    public int BagStagingReserve = 10;        // slots kept free in bags during relays
}
```

Persisted in `Configuration.Organizer : OrganizerSettings { List<OrganizerPlan> Plans; Guid? ActivePlanId; }` with a `Version` bump and `Migrate()` step (same pattern as today). Per-character selection of the active plan via the existing `Profile` (one new property `ActiveOrganizerPlanId`), so the existing override machinery keeps working without UI changes.

Retainer identity in rules is by retainer id (stable), displayed by name via `RunCoordinator.RetainerNames`.

### 2.3 Desired state

`DesiredStateBuilder.Build(snapshot, plan, ctx, infoLookup)`:

- For every `ScannedItem` in scope (bags, armoury, saddlebag, retainers in scope; **dresser excluded**), evaluate rules in order; first match assigns the destination; else `Fallback`.
- `Stay` means "wherever it currently is".
- Items that cannot move are pinned in place with a reason and surface in the preview: gearset members (`ItemContext.GearsetItemIds`), indisposable, items in a retainer's market listing (not in inventory pages anyway), currency/crystals into the saddlebag are allowed (game allows), **soul crystals only to the armoury**, armoury destination only for equipment (target page from `EquipSlotCategory`, to be added to `ItemInfo`).
- Output: `Dictionary<ScannedItem, Destination> desired` plus `pinned` list.

### 2.4 Solver

**Inputs:** snapshot (items + capacities), desired state, plan options.
**Output:** `SolveResult { List<MoveOp> Moves; FeasibilityReport Report; List<Pass> Passes; }`.

1. **Capacity model.** `ContainerCapacity` per (kind, owner, page): `Size` (live `container->Size` when loaded; constants otherwise: bags 4×35, saddlebag 2×35 (+2×35 premium), retainer 7×25, armoury per page 35, rings 50 — constants verified against live sizes at run time and the preview flags a mismatch), `Used`, and **merge headroom**: for each stackable (item, HQ) already present, `StackSize − Quantity` summed. An incoming stack consumes `ceil(max(0, qty − headroom) / StackSize)` new slots; headroom is consumed first. This is the "credit merge headroom" rule.
2. **Diff.** For each item whose destination ≠ current container: one logical move `(item, from, toDestination)`. Same-destination items already there are untouched.
3. **Net slot delta per container.** `delta = incoming_slots − outgoing_slots` using the headroom model above; **feasible iff free + outgoing ≥ incoming** for every container. Infeasible → `FeasibilityReport` lines like *"Retainer Kima'hri needs 23 slots, only 9 free (14 short)"* with the top offending rule. **The preview shows this and the Run button is disabled** until the user edits rules or frees space. Nothing fails mid-run for capacity reasons.
4. **Relay legs.** A move is *direct* if it is bags/armoury ↔ saddlebag, or bags/armoury ↔ retainer R. Retainer↔retainer, retainer↔saddlebag, saddlebag↔retainer become two legs: `RelayOut` (into bags) then `RelayIn` (out of bags). Legs of the same logical move share a `MoveId`.
5. **Ordering** (keeps bag staging from overflowing):
   1. bag-outbound direct moves first, grouped by destination container (frees bag slots);
   2. armoury-outbound and armoury-inbound direct moves;
   3. relayed moves in **waves**: each wave's `RelayOut` set is sized so that `bagFree − BagStagingReserve ≥ slotsNeeded(wave)`; wave = `RelayOut` (open source) → `RelayIn` (open destination). Waves are ordered to minimise container reopenings (all sources for one destination together).
   4. saddlebag/retainer-inbound direct moves last (they consume space we just freed).
6. **Multi-pass fallback.** If a wave cannot fit even with an empty reserve, the solver emits `Passes = [pass1, pass2, …]` and the preview says *"Needs 2 passes: after pass 1 the bags are re-scanned and the remaining N moves run"*. The executor runs pass 1, re-snapshots, re-solves, continues.
7. **Merge planning** at the destination: if `MergeStacksAtDestination`, the target slot for a stackable is an existing partial stack (merge via `MoveItemSlot` onto it, which the game merges), else an empty slot chosen at execution time (destination slots are *never* planned from cached data; see 2.5).
8. **Splits** are out of scope for v1: whole stacks move. If a stack cannot fit even after headroom (e.g. 999 crystals into a container with 0 free and headroom 300), the item stays and is reported. (Open question 3.)

All of this is pure and gets tests: headroom accounting, feasibility report text, relay wave sizing, ordering invariants ("bag usage never exceeds size − reserve at any step", proven by simulating the op list against the capacity model).

### 2.5 Executor

**Shared core (refactor, behaviour-preserving):** extract from `ExecutionEngine` a generic `ExecutionCore<TOp, TResult>` with hooks: `IsAvailable(op)`, `Execute(op)`, `Park(op, reason)`, rate limit, consecutive-failure abort, cancellation. `ExecutionEngine` becomes a thin adapter. Existing engine tests pass unchanged.

**`IMoveActions` (game surface, `src/Gleam/Game/MoveActions.cs`):**

```csharp
bool IsOpen(ContainerKind kind, ulong ownerId);               // reuse GameActions.IsContainerAvailable
ContainerCapacity ReadCapacity(ContainerKind kind, ulong ownerId);
SlotRef? FindSlot(...);                                        // reuse
SlotRef? FindLanding(Destination to, uint itemId, int qty, bool hq, ISet<SlotRef> reserved); // partial stack first, else first empty slot on the right page
Task<MoveOutcome> MoveAsync(SlotRef from, SlotRef to, uint itemId, int qty, CancellationToken ct);
```

`MoveAsync` = `InventoryManager.MoveItemSlot` (as `StackMerger` does) then **confirmation**: wait for `InventoryItemMovedArgs` for the source slot **or** poll `ReadSlot(from)`/`ReadSlot(to)` until the source is empty/reduced and the destination holds the item, with the same "loaded container only, two checks two seconds apart" fallback used by `RunAndAwaitRemoval`. Same rate limit source (`Callbacks.RateLimitMs`).

**`MoveBatch`** = ops sharing a `SessionContext`: `BagsOnly | Saddlebag | Retainer(id)`. Batches are the unit the pilot travels for. Within a batch the executor re-validates the *source* by identity (`FindSlot`, exactly like discards) and resolves the *destination slot live* (`FindLanding`) right before the call, so cached retainer slot numbering never matters.

**Failure policy:** a refused move parks the op with the game's reason (e.g. destination full, item cannot be stored with a retainer); three consecutive failures abort the batch, not the run; the preview's feasibility should make capacity failures rare.

**Session handling:** `AutoPilot.Organizer.cs` reuses `SaddlebagAsync`-style open, `EnsureRetainerListAsync`, `OneRetainerAsync`'s open/close steps (refactored into `WithRetainerOpenAsync(id, body)` and `WithSaddlebagOpenAsync(body)`), and `RecoverUiAsync`. Without hands-free, the coordinator parks batches whose context is closed and resumes when `AddonWatcher.ContainerOpened` fires (same as discards today).

**History:** a separate `organizer-history.jsonl` via the same `JsonLinesRunLog`/`ReliableTextStorage`, so the discard history schema is untouched.

### 2.6 Preview UI

`OrganizerWindow` (styled with `Ui.*`):

1. **Plan bar:** active plan dropdown, Edit, New, Duplicate.
2. **Rules editor** (collapsible): ordered list with drag handles (up/down buttons in ImGui), each row = name · predicate summary pills · destination; inline editor uses the same chips/pills as the main window for tags, HQ, materia; retainers by name.
3. **Preview** (the default view after "Preview"):
   - **Per-container end state cards**: `Retainer Kima'hri · 175 slots · 162 → 171 used · +9` with a slim bar (reuse `Ui.Progress`), red when over capacity, with the shortfall line from the feasibility report.
   - **Moves table** grouped by batch/session: `Open saddlebag (12 moves)`, `Summon Kima'hri (31 moves)`, `Summon Po'tato (8 moves, 3 relayed via bags)`; row = icon · name · qty · from → to · leg tag (`via bags`). Sortable headers reused from the main window.
   - **Summary footer**: `51 moves · 3 containers to open · 1 pass` or `Needs 2 passes` / `Blocked: Retainer X is 14 slots short`.
   - Primary button **Organize** (disabled while infeasible), plus hands-free variant when the pilot is available, mirroring the current footer.
4. Pinned items (cannot move) listed under a "Not moved" note with reasons, like today's "Not listed".

### 2.7 Migration / refactor steps (one commit each)

1. **Extract `InventorySnapshotService`** from `RunCoordinator` (scan + offline fold + `AddMarketPricesAsync` + retainer names). `RunCoordinator` calls it. No behaviour change; existing tests green.
2. **Add `EquipSlotCategoryId` and armoury page mapping** to `ItemInfo`/`ItemDatabase` (needed for armoury destinations). Tests for the mapping.
3. **Extract `ExecutionCore`** from `ExecutionEngine`; engine delegates. Engine tests unchanged.
4. **`ContainerCapacity` + live capacity reads** (`MoveActions.ReadCapacity`, `container->Size`). Unit tests for headroom math.
5. **`MoveActions.MoveAsync` with confirmation**; `StackMerger` becomes a wrapper over it (merge behaviour preserved, now confirmed). Spike button in the troubleshooting window: "move one stack bags → saddlebag and back".
6. **Organizer model + config** (`OrganizerPlan`, `Destination`, predicates, `Configuration.Organizer`, `Version` bump, migration). Serialization round-trip test.
7. **`DesiredStateBuilder`** with pinned reasons. Tests.
8. **`MoveSolver` v1**: direct moves, headroom accounting, feasibility report. Tests.
9. **Solver relays and ordering**: relay legs, waves, staging reserve, multi-pass. Simulation tests proving the bag never overflows.
10. **`MoveExecutor` + `OrganizerCoordinator`**: batches, parking, resume on container open, history file. FakeMoveActions tests.
11. **`OrganizerWindow`**: rules editor.
12. **`OrganizerWindow`**: preview (end-state cards, move table, feasibility, Organize button). Command `/gleam organize`, title-bar button on the main window.
13. **Hands-free organizer legs** in `AutoPilot.Organizer.cs`: container-keyed sessions, reusing bell/saddlebag steps.
14. **Polish**: plain-language messages, memory/notes, in-game verification pass.

Steps 1–5 are pure refactors and infrastructure; nothing user-visible changes until step 11.

### 2.8 Open questions and risks

**Decisions taken (2026-09-08):**

1. **Rule scope:** rules act on items everywhere — bags, armoury, saddlebag and retainers in scope.
2. **Destinations:** a rule may name one retainer or say "any retainer with room"; the solver prefers retainers already holding that item.
3. **Splitting:** whole stacks only in v1. A stack that cannot fit after merge headroom stays and is reported.
4. **Armoury:** a destination in v1; equipment goes to its slot page (needs the equip-slot mapping in item data).
5. **Never touch:** means never discard or sell; moving is allowed, and a rule may target the list explicitly.
6. **UI:** a separate organizer window in the same style, opened from a main-window title-bar button and `/gleam organize`.
7. **Feasibility:** the Run button stays disabled while any container would overflow; the preview names the container and the shortfall.
8. **Relays:** retainer-to-retainer and retainer-to-saddlebag moves relay through the bags in waves sized to a staging reserve; extra passes are shown in the preview.

**Risks found in Phase 1:**

- `MoveItemSlot` returns immediately; confirmation must be observed, and cross-container moves may be reported as `InventoryItemMovedArgs` rather than removed/added (the market listing case proved events are not uniform). The executor polls both slots as ground truth.
- Whether `MoveItemSlot` accepts armoury ↔ retainer and armoury ↔ saddlebag directly needs a spike; if not, those become relays.
- Live capacity constants for premium saddlebag pages (4100/4101) depend on the account; capacity must be read live when the container is open and the preview must say when it is estimating from a cache.
- Allagan Tools cache can be stale (items moved by hand since). Existing identity re-validation covers execution; the preview should show a "last seen" hint for cached containers.
- Relay staging shares bag space with the discard pilot's "brought home" follow-ups; the two features must not run concurrently (single `IsRunning` gate in `RunCoordinator`, extended to the organizer).

### 2.9 Borrowed from other plugins (2026-09-09) — to verify in game

1. **Bag highlighting** (`BagHighlighter`): with the review open, ticked items glow gold in Inventory / InventoryLarge / InventoryExpansion, the retainer inventory and the saddlebag; with the organizer preview open, items to move glow blue. Check the tint follows the game's sort order (display position, not raw slot), survives tab switches, and clears when the window closes. Not yet covered: the armoury chest.
2. **Keep N in the bags** on an organizer rule: whole stacks only; smallest stacks stay first; one oversized stack stays.
3. **Export / Import** in the organizer plan bar: text starts with `GLEAM1:` (layouts shared before the rename start with `TIDYUP1:` and still import); retainers the importer does not own become "any retainer".
4. **List in stacks of N** (Settings, market board preset): each piece is one listing; when a retainer's slots run out the remainder is reported as "partly listed" and waits.
5. **Leave stacks of N or more alone** (Settings → More → What counts as junk, default 200): such rows show as hand-pick with the reason; 0 turns it off.
6. **Protected items**: Ultimate tokens, the special earrings, Ceruleum Tank and Magitek Repair Materials (curated.json) plus Ultimate weapons recognised by shape are never listed.
7. **After ventures** (Settings): with AutoRetainer, after each retainer's ventures Gleam discards default-ticked bag rows and prints one chat line; it always hands the turn back.
