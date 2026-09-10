# Gleam

A Dalamud plugin for Final Fantasy XIV that cleans and organizes your inventory, and does the walking itself.

- **Clean.** Gleam finds junk in your bags, armoury chest, saddlebag, retainers and glamour dresser, and shows it all in one list. You choose whether it is sold on the market board, sold to a vendor or discarded. Nothing happens until you press the button.
- **Organize.** Layouts move the things you keep to where you want them: materia to the saddlebag, spare gear to a retainer, gear-set pieces to the armoury chest. Organizing never discards or sells anything.

Once you press the button, Gleam opens the saddlebag, travels to an inn for your retainers and the dresser, and visits your Grand Company or a merchant when it needs to.

## Before you install

Gleam moves your character and clicks through the game's menus for you. Square Enix's terms of service do not allow third-party tools, and automation is what they act against most. Use Gleam at your own risk.

Organizing is new and has not yet had a full test in game. It never throws anything away. The worst case is an item left in your bags or a run that stops part-way.

## Install

1. In game, open Dalamud's settings with `/xlsettings` and go to **Experimental**.
2. Under **Custom Plugin Repositories**, paste this URL, tick it, and press save:

   ```
   https://raw.githubusercontent.com/sleousis/Gleam/main/repo.json
   ```

3. Open the plugin installer with `/xlplugins`, search for **Gleam** and install it.

Gleam needs these plugins, which Dalamud's own list offers:

- **vnavmesh** walks your character from place to place. Gleam cannot do its job without it.
- **Lifestream** teleports between aetherytes and into inns. Without it, start a run while you are already in an inn.

These are optional:

- **AutoRetainer** lets Gleam discard junk after each retainer's ventures.
- **Allagan Tools** shows what your retainers hold without visiting them.

Market prices come from [Universalis](https://universalis.app) and need an internet connection.

### Coming from a version before 0.10

Gleam used to be called TidyUp internally, and Dalamud still sees that old copy as a separate plugin. To switch:

1. Install Gleam from the list.
2. Remove the old copy, version 0.9.x.

Your settings, layouts and history come across the first time the new copy starts.

## Using it

| Command | What it does |
| --- | --- |
| `/gleam` or `/gl` | Opens the list |
| `/gleam organize` | Opens your layouts |
| `/gleam history` | Shows what Gleam did, with how to get things back where possible |
| `/gleam settings` | Opens the settings |
| `/gleam stop` | Stops whatever is running |
| `/gleam selftest` | Checks that Gleam can still find everything it uses in the game, without touching any item |
| `/gleam report` | Copies a bug report to your clipboard |

After a game patch, Gleam pauses hands-free runs until it has been checked on that patch. Runs you start by hand keep working. Press **Go ahead on this patch** if you want hands-free runs anyway.

## Something is not working

Run `/gleam selftest`, then `/gleam report`, and [open an issue](https://github.com/sleousis/Gleam/issues/new/choose) with the report pasted in. The report contains no character or retainer names.

## Licence

MIT
