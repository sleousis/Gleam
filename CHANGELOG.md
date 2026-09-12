# Changelog

Each release's section is shown in Dalamud's plugin installer and on the GitHub release page. The release
workflow refuses a tag whose version has no section here.

## 0.11.5

- The retainer's "no buyback once recalled" question is recognised again. 0.11.3 read it from a part of the window that was still empty when it appeared, so a trip stopped at the first retainer that had sold something and left the question open.
- If that question is still open when a trip ends, Gleam answers it and dismisses the retainer. Any other question is left for you.

## 0.11.4

- A Stop pressed while a retainer is still greeting you now dismisses that retainer too. After a trip Gleam clicks through the retainer's greeting (never a cutscene), chooses Quit, and keeps going until you have left the bell.
- The bag window the game opens beside a retainer is closed after the trip, unless it was already open when the trip began.

## 0.11.3

Safety fixes for hands-free trips.

- Stopping a trip now leaves the game tidy: the retainer is dismissed, and the shop, dresser, saddlebag and retainer windows are closed. Before, Stop only stopped the walking.
- Gleam reads a yes/no question before answering it. On its own it answers only the retainer's "no buyback once recalled" question. Anything else, such as a party invite at the bell, is left for you and the trip stops.
- An item already handed to a retainer to be sold, or taken out of the glamour dresser, is finished even if you press Stop at that moment. If the sale still fails, the message says which retainer has the item.
- In the simple view the Clean button now says "everywhere" when the run will travel, and "Clean here only" is offered there too. The button's tooltip mentions the teleports to a merchant or your Grand Company.
- The gamepad no longer starts a run, and it only moves through the list while Dalamud's gamepad navigation has the window. The keyboard shortcut is now Ctrl+Enter instead of Enter.
- During a run Gleam reads your bags and the game's windows only on the game's own thread, and it works from copies of your layout and lists, so editing them while it works cannot upset it.

## 0.11.2

- The big numbers on the stats page are sharp now. They used to be the normal font stretched, which blurred them.

## 0.11.1

- If you kept using the older copy (0.9.x) after installing the renamed Gleam, what it saved since then now comes across once you remove it: its newer settings (your previous ones are kept as a backup) and the history it recorded.
- The warning about the older copy now says that /gleam opens the older copy until it is removed.

## 0.11.0

- A new stats page, "Your Gleam in numbers", opens from the chart button in the title bar or with /gleam stats. It shows bag slots freed, gil from sales, market listings and time saved. It also shows a chart of what Gleam did day by day, where the junk came from, how full your bags have been, which rules you keep or untick, where the organizer put things, your last hands-free trip, a year of activity and milestones.
- While a run is going, the page follows it live: totals count up, today's bar grows, and moved items travel across the organizer's map.
- Every figure has one definition, shown in its tooltip. Market listings count as "listed", never "earned", and time saved is labelled as an estimate with its formula.
- Gleam now keeps a small journal of runs, trips, bag fullness, rule choices and seals. It stays on your PC, and "Reset statistics" starts the counting again without touching the history.

## 0.10.0

- The old working name, TidyUp, is gone from everything, including the name Dalamud uses to track the plugin. Because of that, Dalamud treats this version as a new plugin. Install Gleam from the list, then remove the old copy, version 0.9.x. Your settings, layouts and history come across the first time the new copy starts.
- Layouts exported from earlier versions still import.
- The hidden /tidyup command is gone. Use /gleam or /gl.
- After an update, Gleam shows what's new once, at the top of its own window. Fresh installs skip it.
- The plugin description and the first screen now say plainly that Gleam automates the game, which Square Enix's terms do not allow.
- Organizing is marked as a preview until it has had a full test in game.

## 0.9.5

- Gleam can now run hands-free on Japanese, German and French game clients as well as English ones. It matches item-menu entries by every text id the game uses for them. It recognises NPC menu entries by reading them back into English. It accepts a sale or discard prompt when the prompt uses the item's inflected name.
- After a game patch Gleam has not been checked on, hands-free runs pause. Runs you start by hand keep working. "Go ahead on this patch" lifts the pause for that patch only.
- `/gleam selftest` checks that Gleam can still find every menu entry, NPC, object and place it uses, without touching any item.
- `/gleam report`, or "Copy a bug report" in Settings, copies versions, settings, recent failures and the last self-test to paste into a bug report. The report contains no character or retainer names.
- Gleam now checks the price of each market listing after posting it. A listing at the wrong price is reported as failed, and Gleam stops listing further items.
- Expert Delivery now turns in the exact piece that was planned, not the first item with the same name.
- The move history now records which layout rule moved each item.
- Both histories now add one line per item instead of rewriting the whole file each time.
- When a storage opens, moves that were waiting for it run one round at a time. A later round no longer runs before an earlier one has finished.

## 0.9.4

- Background re-scans keep your ticks, and new items start unticked. Split stacks are only merged when you press "Look again".
- The organizer leaves alone the retainers and places you have excluded. It fills named retainers first and checks the armoury page by page.
- A run over the soft cap asks you to press again before it starts. The cap values items at the higher of the vendor price and the market price.
- Exporting to Discard Helper now merges your lists into its file instead of replacing it.
- Logging out, or turning off cleaning or organizing, now stops everything that belongs to it.

## 0.9.3

- Fixed every way the review found that 0.9.2 could lose an item. Only items a rule suggested count as junk or get ticked in bulk. Stopping a run no longer leaves work queued. Unattended runs only take what a rule ticks by default. Relay moves take exactly the stack they placed. Gear stays protected while gear sets or glamour plates have not loaded.

## 0.9.2

- Hands-free now clicks through a retainer's greeting instead of waiting on it until it times out.

## 0.9.1

- Retainers are shown by name instead of by a number.

## 0.9.0

- First public release.
