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
    head = 19 * SCALE + ITEM + w("Sell on the market board") + ITEM + w("89 items") + ITEM + link
    check("outcome card", W, head, card, "verb, count and link collide before the worth is dropped")

    # ---- review table: fixed columns plus a workable stretch
    fixed = 24 + 30 + 128 + 96 + 24                 # tick, icon, action, market, cell padding
    check("review table", W, fixed + 160 * SCALE, inner, "item name column falls under 160px")

    # ---- run screen bars
    bar = min(max(W * 0.6, 260 * SCALE), 720 * SCALE)
    check("run bar", W, bar, inner, "bar wider than the window")
    if bar < 260 * SCALE:
        problems.append(f"run bar at {W}px: {bar:.0f} is below the readable floor")

print(f"checked {len(WIDTHS)} widths: {', '.join(str(x) for x in WIDTHS)}")
if problems:
    print("\nPROBLEMS")
    for p in problems: print(" -", p)
else:
    print("\nNo overlaps or squeezed columns at any checked width.")
