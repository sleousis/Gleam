# Changelog

Each release's section is shown in Dalamud's plugin installer and on the GitHub release page. The release
workflow refuses a tag whose version has no section here.

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
