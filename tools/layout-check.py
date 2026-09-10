"""Checks the layout maths the plugin uses, at the window widths a player can actually make.

This is not a picture: it is the same arithmetic the C# does, run at several widths, reporting anything
that would overlap, run off the edge, or be squeezed to nothing.
"""
SCALE = 1.0
PAD = 14.0 * SCALE          # WindowPadding.X
ITEM = 8.0 * SCALE          # ItemSpacing.X
FRAME_PAD = 8.0 * SCALE
FRAME_H = 22.0 * SCALE
CHAR = 6.6 * SCALE          # rough advance for the body font at 13px

def w(text):
    return len(text) * CHAR

WIDTHS = [560, 700, 860, 1100, 1600]     # minimum size, default, and wide
problems = []

def check(name, width, used, limit, detail=""):
    if used > limit + 0.5:
        problems.append(f"{name} at {width}px: needs {used:.0f}, has {limit:.0f}. {detail}")

for W in WIDTHS:
    inner = W - PAD * 2

    # ---- header: logo, title column, pinned mode switch, right-hand segmented
    logo = 36 * SCALE + 10 * SCALE
    switch = w("Clean") + w("Organize") + FRAME_PAD * 4 + 20 * SCALE
    for label, right in [("clean/simple", 0.0),
                         ("clean/advanced", w("Sell on market board") + w("Sell to vendors") + w("Discard all") + FRAME_PAD * 6 + 20 * SCALE),
                         ("organize/advanced", w("What will move") + w("Rules") + FRAME_PAD * 4 + 20 * SCALE)]:
        right_edge = W - PAD - (right + 16 * SCALE if right > 0 else 0)
        room = right_edge - switch - PAD - logo
        beside = room >= 140 * SCALE
        if beside:
            column = min(max(room, 140 * SCALE), 300 * SCALE)
            check(f"header {label}", W, PAD + logo + column + switch, right_edge, "mode switch would run under the right-hand controls")
        else:
            # reflowed onto its own line under the header, so it only has to fit the window
            check(f"header {label} (reflowed)", W, switch, inner, "mode switch too wide even on its own line")

    # ---- cleaning footer: summary, sort tick, here-only link, primary button
    button = min(max(inner * 0.42, 150 * SCALE), 240 * SCALE)
    sort = w("Sort bags afterwards") + FRAME_H + 4 * SCALE + ITEM * 2
    here = w("Clean here only") + FRAME_PAD * 2 + ITEM
    for label, need in [("simple", button), ("advanced", sort + here + button)]:
        wraps = need + 160 * SCALE > inner
        check(f"clean footer {label}", W, need, inner, "controls alone do not fit even after wrapping")
        if wraps and W >= 1100:
            problems.append(f"clean footer {label} at {W}px: wrapping to its own line at a wide size")

    # ---- outcome card header: tick, verb, count, worth, link
    link = w("Hide the list") + FRAME_PAD * 2 + 8 * SCALE
    card = inner - 24 * SCALE                       # card padding
    GLYPH = 14 * SCALE + 6 * SCALE                  # an action glyph and the gap after it
    head = 19 * SCALE + ITEM + GLYPH + w("Sell on the market board") + ITEM + w("89 items") + ITEM + link
    check("outcome card", W, head, card, "verb, count and link collide before the worth is dropped")

    # ---- review table: fixed columns plus a workable stretch
    fixed = 24 + 30 + 168 + 96 + 24                 # tick, icon, action (glyph + word), market, cell padding
    check("review table", W, fixed + 160 * SCALE, inner, "item name column falls under 160px")

    # the action cell: glyph, then either the word or a dropdown showing the widest word
    for verb in ("Expert delivery", "Market board", "Discard", "Sell", "Desynth"):
        check("action cell", W, GLYPH + w(verb), 168 * SCALE, f"'{verb}' does not fit beside its glyph")
    check("action dropdown", W, GLYPH + w("Expert delivery") + FRAME_H + FRAME_PAD * 2, 168 * SCALE,
          "the action dropdown does not fit the column")

    # ---- settings: every card is the window less its padding, less the card's own
    card = inner - 24 * SCALE
    check_box = lambda t: w(t) + FRAME_H + 4 * SCALE

    # what would you like Gleam to do: two ticks with a wide gap between them
    check("settings purpose", W, check_box("Clear out my junk") + 24 * SCALE + check_box("Put my things away"), card,
          "the two purpose ticks collide")

    # what should happen to junk: the preset group, then the market stack row
    seg = sum(w(t) + FRAME_PAD * 2 + 6 * SCALE for t in ("Sell on market board", "Sell to vendors", "Discard all")) + 8 * SCALE
    check("settings presets", W, seg, card, "the preset pills run off the card")
    check("settings market stack", W, w("Put it up for sale in stacks of") + ITEM + 70 * SCALE + ITEM + w("(the whole stack)"), card,
          "the stack-size row runs off the card")

    # where should Gleam look: the ticks wrap, so only the widest single one has to fit
    for name in ("Bags", "Armoury chest", "Chocobo saddlebag", "Retainer", "Glamour dresser"):
        check("settings container tick", W, check_box(name), card, f"'{name}' does not fit on a line of its own")

    # what Gleam needs: pill, name, and the sentence beside it or wrapped under it
    for pill, name, what in [("installed", "vnavmesh", "Walks you to the bell, the dresser and the merchant."),
                             ("missing", "Lifestream", "Teleports you to the places a run needs.")]:
        head = w(pill) + FRAME_PAD * 2 + 16 * SCALE + ITEM + w(name)
        check("settings requirement head", W, head, card, "the plugin name alone does not fit")
        # when it does not fit beside the name the C# drops it under, indented, where it wraps freely;
        # what has to hold is that the indented column is still wide enough to read.
        if head + ITEM + w(what) > card:
            check("settings requirement wrapped", W, 220 * SCALE, card - 6 * SCALE,
                  "the wrapped sentence has under 220px to wrap into")

    # after your retainers, and the run-finished tick
    check("settings ventures", W,
          check_box("Throw away junk a finished venture leaves in my bags") + ITEM + w("not installed") + FRAME_PAD * 2, card,
          "the venture tick and its pill collide")
    check("settings finish", W, check_box("Sort bags afterwards"), card, "the sort tick does not fit")

    # the page footer: a tick on the left, a link pushed to the right
    foot_tick = check_box("Show me every setting") + 24 * SCALE + check_box("Hold still")
    foot_link = w("Something is not working") + FRAME_PAD * 2
    check("settings footer", W, foot_tick + ITEM + foot_link, inner, "the footer ticks and the help link collide")

    # the folds, indented under their header
    fold = inner - 12 * SCALE
    check("fold rules stack guard", W, w("Leave stacks of") + ITEM + 70 * SCALE + ITEM + w("or more alone (off)"), fold,
          "the large-stack row runs off the fold")
    check("fold notifications slider", W, 180 * SCALE + ITEM + w("Nudge when bags are this full"), fold,
          "the fullness slider and its label collide")
    check("fold notifications tick", W, check_box("Open the review when a saddlebag, retainer or dresser opens"), fold,
          "the auto-open tick does not fit")
    check("fold discard helper", W,
          200 * SCALE + ITEM + w("Give it mine") + FRAME_PAD * 2 + ITEM + w("Pick a file") + FRAME_PAD * 2 + ITEM + w("Added 12."), fold,
          "the Discard Helper row runs off the fold")

    # the two list editors: search box, scope tick, then the table's fixed columns
    check("list editor search", W, 240 * SCALE + ITEM + check_box("This character only"), card,
          "the search box and its scope tick collide")
    check("list editor table", W, (28 + 90 + 70) * SCALE + 160 * SCALE, card, "the item name column falls under 160px")

    # ---- filter chips: the rows wrap now, so only the widest single chip has to fit a line
    CHIP = lambda text, glyph=True: w(text) + 18 * SCALE + ((14 * SCALE + 5 * SCALE) if glyph else 0)
    chip_indent = w("Containers") + ITEM
    for text in ("Bags 24/24", "Armoury chest 156/156", "Chocobo saddlebag 178/178",
                 "Retainer 178/178", "Glamour dresser 40/40"):
        check("container chip", W, CHIP(text), inner - chip_indent, f"'{text}' does not fit a line of its own")
    for text in ("Gear 151/151", "Materia 35/35", "Materials 13/13", "Consumables 11/11",
                 "Crystals 99/99", "Housing 1/1", "Collectibles 24/24", "Other 47/47"):
        check("type chip", W, CHIP(text), inner - chip_indent, f"'{text}' does not fit a line of its own")
    # the two trade chips wrap as a pair, so they need a line between them
    pair = CHIP("Tradeable 666") + CHIP("Untradeable 1992") + ITEM
    check("trade chip pair", W, pair, inner - chip_indent, "the tradeable pair does not fit a line of its own")

    # ---- market cell: the game's coin plus the figure, in a fixed column
    check("market cell", W, 17 * SCALE + 4 * SCALE + w("99,670"), 96 * SCALE, "a six-figure price and its coin overrun the column")

    # ---- section header: glyph, padded title, count pill
    tree = 22 * SCALE
    head_pill = w("178 / 178") + FRAME_PAD * 2
    check("section header", W, tree + w("    Retainer: Kima'hri") + 22 * SCALE + head_pill, inner,
          "the section title and its count pill collide")

    # ---- run screen bars
    bar = min(max(W * 0.6, 260 * SCALE), 720 * SCALE)
    check("run bar", W, bar, inner, "bar wider than the window")
    if bar < 260 * SCALE:
        problems.append(f"run bar at {W}px: {bar:.0f} is below the readable floor")

    # ---- stats page: the header's pickers, the four totals, the panel rows
    SCROLL = 14 * SCALE
    body = inner - SCROLL
    seg = sum(w(t) + FRAME_PAD * 2 + 6 * SCALE for t in ("7 days", "30 days", "1 year", "All time")) + 8 * SCALE
    pickers = seg + ITEM + 140 * SCALE
    title = max(w("Your Gleam in numbers"), w("This character · since 10 Sep 2025"))
    beside = inner >= 46 * SCALE + title + 16 * SCALE + pickers
    if not beside:
        check("stats pickers (own line)", W, pickers, inner, "the period and character pickers do not fit even on their own line")

    cols = 4 if body >= 760 * SCALE else 2
    tile = (body - 10 * SCALE * (cols - 1)) / cols
    check("stats tile label", W, 12 * SCALE + 20 * SCALE + w("Listed on the market"), tile - 12 * SCALE, "a tile's label runs off the tile")
    room = tile * 0.58 - 18 * SCALE
    check("stats tile number", W, w("12,345,678") * 1.15, room, "a large gil total runs into the tile's sparkline even at its smallest")

    wide = body >= 820 * SCALE
    halves = (body - 10 * SCALE) / 2 - 24 * SCALE if wide else body - 24 * SCALE
    check("stats rule row", W, w("Crafting materials no recipe uses") + ITEM + w("1,234 · 42%"), halves, "a rule's name runs into its figures")
    check("stats ribbons", W, w("Saddlebag") + 12 * SCALE + 120 * SCALE + w("A long retainer") + w("1,234") + 22 * SCALE, halves,
          "the organizer's ribbons are squeezed between their labels")
    year = body - 24 * SCALE
    cell = min(max((year - 3 * SCALE * 52) / 53, 5 * SCALE), 13 * SCALE)
    check("stats year grid", W, 53 * (cell + 3 * SCALE) - 3 * SCALE, year + 12 * SCALE, "the year grid runs past its panel")

print(f"checked {len(WIDTHS)} widths: {', '.join(str(x) for x in WIDTHS)}")
if problems:
    print("\nPROBLEMS")
    for p in problems: print(" -", p)
else:
    print("\nNo overlaps or squeezed columns at any checked width.")
