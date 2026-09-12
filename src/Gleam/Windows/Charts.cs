using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;

namespace Gleam.Windows;

/// <summary>
/// The stats page's charts, drawn straight onto ImGui's draw list in Gleam's colours. Each chart keeps a
/// little state by id: its parts ease towards their values, so a chart grows in when it first appears and
/// reshapes, rather than redraws, when the numbers behind it change. With "Reduce motion" everything arrives
/// at once. Callers reserve the space; these only draw into the rectangle they are given.
/// </summary>
public static class Charts
{
    private sealed class State
    {
        public double First;
        public double Last;
        public readonly Dictionary<int, float> Values = new();
        public readonly List<Particle> Particles = new();
    }

    private sealed class Particle
    {
        public int Lane;
        public float T;
        public float Speed;
    }

    private static readonly Dictionary<string, State> states = new();
    private static readonly Random random = new();
    private static readonly Vector4 Grid = new(1f, 1f, 1f, 0.06f);

    /// <summary>Forgets charts nothing has drawn for a while; one that comes back grows in again, as it would anyway.</summary>
    internal static void Sweep(double now, double idle)
    {
        foreach (var (k, s) in states) if (now - s.Last > idle) states.Remove(k);
    }

    internal static void Reset() => states.Clear();

    /// <summary>A chart's state. One that has been off screen for a moment starts over, so it grows in again.</summary>
    private static State Get(string id)
    {
        var now = ImGui.GetTime();
        if (!states.TryGetValue(id, out var s) || now - s.Last > 0.5)
        {
            s = new State { First = now };
            states[id] = s;
        }
        s.Last = now;
        return s;
    }

    private static float Since(State s) => (float)(ImGui.GetTime() - s.First);

    /// <summary>Eases one part of a chart towards its value. A new part starts from nothing, after its stagger.</summary>
    private static float Ease(State s, int key, float target, float delay = 0f, float speed = 9f)
    {
        if (Ui.Reduced) { s.Values[key] = target; return target; }
        var v = s.Values.GetValueOrDefault(key);
        if (Since(s) >= delay)
        {
            var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
            v += (target - v) * (1f - MathF.Exp(-speed * dt));
            if (MathF.Abs(v - target) <= 0.0005f * Math.Max(1f, MathF.Abs(target))) v = target;
        }
        s.Values[key] = v;
        return v;
    }

    /// <summary>A value that grows in from nothing the first time it is shown, then follows its target.</summary>
    public static float Grow(string id, int key, float target, float delay = 0f) => Ease(Get(id), key, target, delay);

    private static float Reveal(State s, float seconds) => Ui.Reduced ? 1f : Ui.EaseOut(Math.Clamp(Since(s) / seconds, 0f, 1f));

    public static uint Col(Vector4 c) => ImGui.GetColorU32(c);

    public static Vector4 Fade(Vector4 c, float a) => new(c.X, c.Y, c.Z, c.W * a);

    public static bool Hovered(Vector2 min, Vector2 size) => ImGui.IsMouseHoveringRect(min, min + size, true);

    public static void Text(Vector2 pos, Vector4 color, string text) => ImGui.GetWindowDrawList().AddText(pos, Col(color), text);

    public static float TextWidth(string text) => ImGui.CalcTextSize(text, false, 0).X;

    /// <summary>
    /// A large font, built by the plugin at the size headline numbers are drawn (see <see cref="CreateBigFont"/>).
    /// Drawing the body font larger stretches glyphs rasterised for 17 px and they come out blurred; a font
    /// built big and drawn at or below its own size stays sharp.
    /// </summary>
    public static IFontHandle? BigFont { get; set; }

    /// <summary>How much larger than the body font <see cref="BigFont"/> is built. Big text is never drawn larger than this.</summary>
    public const float BigFontScale = 1.8f;

    /// <summary>Call once from the plugin: <c>Charts.BigFont = Charts.CreateBigFont(pi.UiBuilder.FontAtlas);</c>. Dispose it on unload.</summary>
    public static IFontHandle CreateBigFont(IFontAtlas atlas) =>
        atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(Dalamud.Interface.UiBuilder.DefaultFontSizePx * BigFontScale)));

    private static ILockedImFont? heldBig;
    private static readonly BigFontHold Held = new();

    /// <summary>
    /// Takes the big font for the rest of a page's draw, so each headline number on it does not lock and
    /// release the font again. Dispose at the end of the page.
    /// </summary>
    public static IDisposable HoldBigFont()
    {
        if (heldBig is null && BigFont is { Available: true } handle) heldBig = handle.Lock();
        return Held;
    }

    private sealed class BigFontHold : IDisposable
    {
        public void Dispose()
        {
            heldBig?.Dispose();
            heldBig = null;
        }
    }

    /// <summary>Headline text, <paramref name="scale"/> times the body font's size, from the big font when it is ready.</summary>
    public static void BigText(Vector2 pos, Vector4 color, string text, float scale)
    {
        var size = ImGui.GetFontSize() * Math.Min(scale, BigFontScale);
        if (heldBig is { } held)
        {
            ImGui.GetWindowDrawList().AddText(held.ImFont, size, pos, Col(color), text);
            return;
        }
        if (BigFont is { Available: true } handle)
        {
            using var locked = handle.Lock();
            ImGui.GetWindowDrawList().AddText(locked.ImFont, size, pos, Col(color), text);
            return;
        }
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), size, pos, Col(color), text);
    }

    public static float BigTextWidth(string text, float scale) => TextWidth(text) * scale;

    /// <summary>1,284 stays as it is; 12,300 becomes 12.3k and 1,900,000 becomes 1.9M.</summary>
    public static string Short(long v) => v switch
    {
        >= 1_000_000 => $"{v / 1_000_000d:0.#}M",
        >= 10_000 => $"{v / 1_000d:0.#}k",
        _ => $"{v:N0}",
    };

    /// <summary>A round number just above the peak, so the axis reads 0, 5, 10, 15, 20 rather than 0, 4.6, 9.2.</summary>
    private static float NiceMax(float v)
    {
        var p = MathF.Pow(10, MathF.Floor(MathF.Log10(Math.Max(1f, v))));
        var m = v / p;
        return (m <= 2 ? 2 : m <= 4 ? 4 : m <= 5 ? 5 : 10) * p;
    }

    // ---------- sparkline ----------

    /// <summary>
    /// A small line with a soft fill and its newest value marked. <paramref name="scroll"/> slides it left by
    /// part of a step, for a line that is fed a new value every second and should flow rather than jump.
    /// </summary>
    public static void Sparkline(string id, IReadOnlyList<float> values, Vector2 pos, Vector2 size, Vector4 color, float scroll = 0f, bool ease = true)
    {
        if (values.Count < 3) return;
        var s = Get(id);
        var dl = ImGui.GetWindowDrawList();
        var top = 0.001f;
        for (var i = 0; i < values.Count; i++) top = Math.Max(top, values[i]);
        var n = values.Count;
        var step = scroll > 0 || !ease ? size.X / (n - 2) : size.X / (n - 1);
        Span<Vector2> pts = n <= 256 ? stackalloc Vector2[n] : new Vector2[n];
        for (var i = 0; i < n; i++)
        {
            var v = (ease ? Ease(s, i, values[i]) : values[i]) / top;
            pts[i] = new Vector2(pos.X + (i - scroll) * step, pos.Y + size.Y - 2f - Math.Clamp(v, 0f, 1f) * (size.Y - 5f));
        }
        var limit = pos.X + size.X * Reveal(s, 0.7f);
        var bottom = pos.Y + size.Y;
        ImGui.PushClipRect(pos - new Vector2(0, 4), pos + size + new Vector2(4, 4), true);
        for (var i = 1; i < n; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            if (a.X > limit) break;
            if (b.X > limit) b = Vector2.Lerp(a, b, (limit - a.X) / Math.Max(0.001f, b.X - a.X));
            dl.AddQuadFilled(new Vector2(a.X, bottom), a, b, new Vector2(b.X, bottom), Col(Fade(color, 0.14f)));
            dl.AddLine(a, b, Col(color), 1.6f * Ui.Scale);
        }
        if (limit >= pos.X + size.X - 0.5f)
        {
            var end = step == size.X / (n - 2) ? Vector2.Lerp(pts[n - 2], pts[n - 1], scroll) : pts[n - 1];
            dl.AddCircleFilled(end, 2.6f * Ui.Scale, Col(color));
        }
        ImGui.PopClipRect();
    }

    // ---------- stacked bars ----------

    /// <summary>One bar per bucket, stacked by kind, on a rounded axis. Returns the bar under the pointer, or -1.</summary>
    public static int StackedBars(string id, IReadOnlyList<float[]> stacks, IReadOnlyList<Vector4> colors, Vector2 pos, Vector2 size, Func<int, string?> xLabel)
    {
        var s = Get(id);
        var dl = ImGui.GetWindowDrawList();
        var lineH = ImGui.GetTextLineHeight();
        var plotMin = new Vector2(pos.X + 38f * Ui.Scale, pos.Y + lineH * 0.5f);
        var plotMax = new Vector2(pos.X + size.X, pos.Y + size.Y - lineH - 6f * Ui.Scale);
        var h = plotMax.Y - plotMin.Y;

        var peak = 0f;
        foreach (var st in stacks) peak = Math.Max(peak, st.Sum());
        var yMax = NiceMax(Math.Max(4f, peak * 1.05f));
        if (!s.Values.ContainsKey(-1)) s.Values[-1] = yMax;
        var axis = Math.Max(1f, Ease(s, -1, yMax, 0f, 6f));

        for (var g = 0; g <= 4; g++)
        {
            var y = plotMax.Y - h * g / 4f;
            dl.AddLine(new Vector2(plotMin.X, y), new Vector2(plotMax.X, y), Col(Grid));
            var label = Short((long)MathF.Round(axis * g / 4f));
            Text(new Vector2(plotMin.X - 6f * Ui.Scale - TextWidth(label), y - lineH / 2), Ui.Muted, label);
        }

        var n = Math.Max(1, stacks.Count);
        var step = (plotMax.X - plotMin.X) / n;
        var bw = Math.Max(2f, step * 0.68f);
        var hover = -1;
        if (ImGui.IsMouseHoveringRect(plotMin, new Vector2(plotMax.X, pos.Y + size.Y), true))
        {
            var i = (int)((ImGui.GetIO().MousePos.X - plotMin.X) / step);
            if (i >= 0 && i < stacks.Count) hover = i;
        }

        var stagger = stacks.Count > 40 ? 0.006f : 0.014f;
        for (var i = 0; i < stacks.Count; i++)
        {
            var x0 = plotMin.X + i * step + (step - bw) / 2;
            var baseY = plotMax.Y;
            var dim = hover >= 0 && hover != i ? 0.45f : 1f;
            for (var j = 0; j < stacks[i].Length && j < colors.Count; j++)
            {
                var v = Ease(s, i * 8 + j, stacks[i][j], i * stagger);
                var bh = v / axis * h;
                if (bh < 0.2f) continue;
                dl.AddRectFilled(new Vector2(x0, baseY - bh), new Vector2(x0 + bw, baseY), Col(Fade(colors[j], dim)), Math.Min(2f * Ui.Scale, bw / 3f));
                baseY -= bh;
            }
        }

        // Labels far enough apart to read, and never one crowding the last, which names today.
        var every = Math.Max(1, (int)MathF.Ceiling(56f * Ui.Scale / step));
        for (var i = 0; i < stacks.Count; i++)
        {
            var last = i == stacks.Count - 1;
            if (!last && (i % every != 0 || stacks.Count - 1 - i < every)) continue;
            var label = xLabel(i);
            if (string.IsNullOrEmpty(label)) continue;
            var w = TextWidth(label);
            var x = Math.Clamp(plotMin.X + i * step + step / 2 - w / 2, plotMin.X, plotMax.X - w);
            Text(new Vector2(x, plotMax.Y + 4f * Ui.Scale), Ui.Muted, label);
        }
        return hover;
    }

    // ---------- ring of slices ----------

    /// <summary>A ring split into slices, the total in the middle. The slice under the pointer thickens. Returns it, or -1.</summary>
    public static int Donut(string id, IReadOnlyList<(float Value, Vector4 Color)> slices, Vector2 center, float radius, float thickness, string label, string caption)
    {
        var s = Get(id);
        var dl = ImGui.GetWindowDrawList();
        var total = 0f;
        foreach (var x in slices) total += x.Value;
        dl.AddCircle(center, radius, Col(Grid), 64, thickness);

        var hover = -1;
        var mouse = ImGui.GetIO().MousePos - center;
        var dist = mouse.Length();
        var reach = new Vector2(radius + thickness);
        if (total > 0 && dist > radius - thickness && dist < radius + thickness && ImGui.IsMouseHoveringRect(center - reach, center + reach, true))
        {
            var angle = MathF.Atan2(mouse.Y, mouse.X) + MathF.PI / 2f;
            if (angle < 0) angle += MathF.PI * 2f;
            var f = angle / (MathF.PI * 2f);
            var acc = 0f;
            for (var i = 0; i < slices.Count; i++)
            {
                var w = slices[i].Value / total;
                if (f >= acc && f < acc + w) { hover = i; break; }
                acc += w;
            }
        }

        var sweep = Reveal(s, 0.8f);
        var start = -MathF.PI / 2f;
        var at = 0f;
        for (var i = 0; i < slices.Count; i++)
        {
            var frac = total <= 0 ? 0f : Ease(s, i, slices[i].Value / total);
            var a0 = start + at * MathF.PI * 2f * sweep;
            var a1 = start + (at + frac) * MathF.PI * 2f * sweep;
            at += frac;
            var grow = Ease(s, 100 + i, hover == i ? 1f : 0f, 0f, 16f);
            if (a1 - a0 < 0.03f) continue;
            dl.PathArcTo(center, radius, a0 + 0.012f, a1 - 0.012f, 40);
            dl.PathStroke(Col(slices[i].Color), thickness * (1f + 0.35f * grow));
        }

        const float big = 1.45f;
        BigText(new Vector2(center.X - BigTextWidth(label, big) / 2, center.Y - ImGui.GetFontSize() * big * 0.62f), Ui.Cream, label, big);
        Text(new Vector2(center.X - TextWidth(caption) / 2, center.Y + ImGui.GetFontSize() * 0.5f), Ui.Muted, caption);
        return hover;
    }

    // ---------- lines over time ----------

    /// <summary>One line of a line chart. Points run from x = 0 (the period's start) to 1 (now), y in the chart's units.</summary>
    public sealed record Series(IReadOnlyList<Vector2> Points, Vector4 Color, bool Dashed, bool Fill);

    /// <summary>
    /// Lines drawn left to right as the chart appears, with dots on the first line where <paramref name="marks"/>
    /// fall. Returns the index of the first line's point nearest the pointer, or -1.
    /// </summary>
    public static int Lines(string id, IReadOnlyList<Series> series, float yMax, Vector2 pos, Vector2 size, IReadOnlyList<float> marks)
    {
        var s = Get(id);
        var dl = ImGui.GetWindowDrawList();
        var lineH = ImGui.GetTextLineHeight();
        var plotMin = new Vector2(pos.X + 32f * Ui.Scale, pos.Y + lineH * 0.5f);
        var plotMax = new Vector2(pos.X + size.X - 4f * Ui.Scale, pos.Y + size.Y - 4f * Ui.Scale);
        var w = plotMax.X - plotMin.X;
        var h = plotMax.Y - plotMin.Y;
        for (var g = 0; g <= 4; g++)
        {
            var y = plotMax.Y - h * g / 4f;
            dl.AddLine(new Vector2(plotMin.X, y), new Vector2(plotMax.X, y), Col(Grid));
            var label = $"{yMax * g / 4f:0}";
            Text(new Vector2(plotMin.X - 6f * Ui.Scale - TextWidth(label), y - lineH / 2), Ui.Muted, label);
        }

        Vector2 P(Vector2 p) => new(plotMin.X + Math.Clamp(p.X, 0f, 1f) * w, plotMax.Y - Math.Clamp(p.Y / yMax, 0f, 1f) * h);
        var limit = plotMin.X + w * Reveal(s, 0.9f);
        foreach (var line in series)
        {
            // Placed as they are drawn, rather than into a fresh list of every point on every frame.
            var pts = line.Points;
            if (pts.Count == 0) continue;
            var next = P(pts[0]);
            for (var i = 1; i < pts.Count; i++)
            {
                var a = next;
                var b = next = P(pts[i]);
                if (a.X > limit) break;
                if (b.X > limit) b = Vector2.Lerp(a, b, (limit - a.X) / Math.Max(0.001f, b.X - a.X));
                if (line.Fill) dl.AddQuadFilled(new Vector2(a.X, plotMax.Y), a, b, new Vector2(b.X, plotMax.Y), Col(Fade(line.Color, 0.14f)));
                if (!line.Dashed) dl.AddLine(a, b, Col(line.Color), 1.8f * Ui.Scale);
                else Dashed(dl, a, b, line.Color);
            }
        }

        if (series.Count > 0 && series[0].Points.Count > 0)
        {
            var first = series[0].Points;
            foreach (var x in marks)
            {
                var y = At(first, x);
                var p = P(new Vector2(x, y));
                if (p.X > limit) continue;
                dl.AddCircleFilled(p, 3f * Ui.Scale, Col(Ui.Ink));
                dl.AddCircle(p, 3f * Ui.Scale, Col(series[0].Color), 12, 1.4f * Ui.Scale);
            }
        }

        var hover = -1;
        if (series.Count > 0 && series[0].Points.Count > 0 && ImGui.IsMouseHoveringRect(plotMin, plotMax, true))
        {
            var mx = (ImGui.GetIO().MousePos.X - plotMin.X) / w;
            var best = float.MaxValue;
            for (var i = 0; i < series[0].Points.Count; i++)
            {
                var d = MathF.Abs(series[0].Points[i].X - mx);
                if (d < best) { best = d; hover = i; }
            }
            var hp = P(series[0].Points[hover]);
            dl.AddLine(new Vector2(hp.X, plotMin.Y), new Vector2(hp.X, plotMax.Y), Col(Fade(Ui.Cream, 0.18f)));
            dl.AddCircleFilled(hp, 3.6f * Ui.Scale, Col(series[0].Color));
        }
        return hover;
    }

    private static float At(IReadOnlyList<Vector2> pts, float x)
    {
        if (x <= pts[0].X) return pts[0].Y;
        for (var i = 1; i < pts.Count; i++)
            if (x <= pts[i].X)
            {
                var span = Math.Max(1e-6f, pts[i].X - pts[i - 1].X);
                return pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * (x - pts[i - 1].X) / span;
            }
        return pts[^1].Y;
    }

    private static void Dashed(ImDrawListPtr dl, Vector2 a, Vector2 b, Vector4 color)
    {
        var length = Vector2.Distance(a, b);
        if (length < 0.5f) return;
        var dir = (b - a) / length;
        var dash = 4f * Ui.Scale;
        var gap = 3f * Ui.Scale;
        for (var t = 0f; t < length; t += dash + gap)
            dl.AddLine(a + dir * t, a + dir * Math.Min(length, t + dash), Col(color), 1.5f * Ui.Scale);
    }

    // ---------- ribbons between storages ----------

    public sealed record Ribbon(string From, string To, int Count);

    /// <summary>
    /// Ribbons from where things were to where they went, as wide as how many went. While <paramref name="live"/>,
    /// items travel along them. Returns the ribbon under the pointer, or -1.
    /// </summary>
    public static int Ribbons(string id, IReadOnlyList<Ribbon> ribbons, Vector2 pos, Vector2 size, bool live)
    {
        var s = Get(id);
        var dl = ImGui.GetWindowDrawList();
        var lineH = ImGui.GetTextLineHeight();
        var lefts = ribbons.Select(r => r.From).Distinct().ToList();
        var rights = ribbons.Select(r => r.To).Distinct().ToList();
        var totals = rights.ToDictionary(r => r, r => ribbons.Where(x => x.To == r).Sum(x => x.Count));
        var leftW = lefts.Max(TextWidth) + 12f * Ui.Scale;
        var rightW = rights.Max(r => TextWidth(r) + TextWidth(Short(totals[r]))) + 22f * Ui.Scale;
        var x0 = pos.X + leftW;
        var x1 = pos.X + size.X - rightW;
        float Y(int i, int count) => count == 1 ? pos.Y + size.Y / 2 : pos.Y + lineH + i * (size.Y - 2 * lineH) / (count - 1);
        var max = Math.Max(1, ribbons.Max(r => r.Count));
        var lanes = ribbons.Select(r => (A: new Vector2(x0, Y(lefts.IndexOf(r.From), lefts.Count)), B: new Vector2(x1, Y(rights.IndexOf(r.To), rights.Count)), W: (2f + 12f * r.Count / max) * Ui.Scale)).ToList();

        var hover = -1;
        var mouse = ImGui.GetIO().MousePos;
        if (Hovered(pos, size))
            for (var i = 0; i < lanes.Count && hover < 0; i++)
                for (var k = 0; k <= 20; k++)
                    if (Vector2.Distance(Bez(lanes[i].A, lanes[i].B, k / 20f), mouse) < lanes[i].W / 2 + 3f * Ui.Scale) { hover = i; break; }

        var reveal = Reveal(s, 0.8f);
        for (var i = 0; i < lanes.Count; i++)
        {
            var (a, b, width) = lanes[i];
            var alpha = hover < 0 ? 0.42f : hover == i ? 0.8f : 0.18f;
            var color = Col(Fade(Ui.Mix(Ui.AccentSoft, Ui.Info, 0.35f), alpha));
            var prev = a;
            const int segments = 28;
            for (var k = 1; k <= segments; k++)
            {
                var t = k / (float)segments;
                if (t > reveal) break;
                var p = Bez(a, b, t);
                dl.AddLine(prev, p, color, width);
                prev = p;
            }
        }

        foreach (var name in lefts)
        {
            var y = Y(lefts.IndexOf(name), lefts.Count);
            Text(new Vector2(x0 - 8f * Ui.Scale - TextWidth(name), y - lineH / 2), Ui.Cream, name);
        }
        foreach (var name in rights)
        {
            var y = Y(rights.IndexOf(name), rights.Count);
            Text(new Vector2(x1 + 8f * Ui.Scale, y - lineH / 2), Ui.Cream, name);
            Text(new Vector2(x1 + 14f * Ui.Scale + TextWidth(name), y - lineH / 2), Ui.Muted, Short(totals[name]));
        }

        // Items in transit: born on the busier ribbons more often, and only while something is being moved.
        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        if (live && !Ui.Reduced && reveal >= 1f)
            for (var i = 0; i < lanes.Count; i++)
                if (random.NextDouble() < dt * (0.6 + 2.4 * ribbons[i].Count / max))
                    s.Particles.Add(new Particle { Lane = i, T = 0f, Speed = 0.45f + (float)random.NextDouble() * 0.3f });
        for (var i = s.Particles.Count - 1; i >= 0; i--)
        {
            var p = s.Particles[i];
            p.T += p.Speed * dt;
            if (p.T >= 1f || p.Lane >= lanes.Count || Ui.Reduced) { s.Particles.RemoveAt(i); continue; }
            var at = Bez(lanes[p.Lane].A, lanes[p.Lane].B, p.T);
            dl.AddCircleFilled(at, 2.4f * Ui.Scale, Col(Fade(Ui.Cream, 0.35f + 0.65f * MathF.Sin(MathF.PI * p.T))));
        }
        return hover;
    }

    private static Vector2 Bez(Vector2 a, Vector2 b, float t)
    {
        var mx = (a.X + b.X) / 2;
        var u = 1 - t;
        var x = u * u * u * a.X + 3 * u * u * t * mx + 3 * u * t * t * mx + t * t * t * b.X;
        var y = u * u * u * a.Y + 3 * u * u * t * a.Y + 3 * u * t * t * b.Y + t * t * t * b.Y;
        return new Vector2(x, y);
    }

    // ---------- one bar in segments ----------

    /// <summary>A bar split into segments by weight, labelled where a label fits. <paramref name="active"/> shimmers. Returns the one under the pointer, or -1.</summary>
    public static int Segments(string id, IReadOnlyList<(string Label, float Weight, Vector4 Color)> segments, Vector2 pos, Vector2 size, int active = -1)
    {
        var s = Get(id);
        var dl = ImGui.GetWindowDrawList();
        var total = Math.Max(0.001f, segments.Sum(x => x.Weight));
        var gap = 2f * Ui.Scale;
        var usable = size.X - gap * Math.Max(0, segments.Count - 1);
        var lineH = ImGui.GetTextLineHeight();
        var hover = -1;
        var x = pos.X;
        for (var i = 0; i < segments.Count; i++)
        {
            var w = Ease(s, i, segments[i].Weight / total, i * 0.05f) * usable;
            if (w < 1f) { x += w + gap; continue; }
            var min = new Vector2(x, pos.Y);
            var max = new Vector2(x + w, pos.Y + size.Y);
            if (ImGui.IsMouseHoveringRect(min, max, true)) hover = i;
            dl.AddRectFilled(min, max, Col(Fade(segments[i].Color, hover >= 0 && hover != i ? 0.55f : 1f)), 4f * Ui.Scale);
            if (i == active && !Ui.Reduced)
            {
                var t = (float)(ImGui.GetTime() * 0.8 % 1.0);
                var band = Math.Max(24f * Ui.Scale, w * 0.35f);
                var cx = min.X - band + (w + band * 2) * t;
                ImGui.PushClipRect(min, max, true);
                var clear = Col(new Vector4(1, 1, 1, 0));
                var shine = Col(new Vector4(1, 1, 1, 0.4f));
                dl.AddRectFilledMultiColor(new Vector2(cx - band, min.Y), new Vector2(cx, max.Y), clear, shine, shine, clear);
                dl.AddRectFilledMultiColor(new Vector2(cx, min.Y), new Vector2(cx + band, max.Y), shine, clear, clear, shine);
                ImGui.PopClipRect();
            }
            var label = segments[i].Label;
            if (TextWidth(label) + 10f * Ui.Scale < w)
                Text(new Vector2(min.X + (w - TextWidth(label)) / 2, min.Y + (size.Y - lineH) / 2), Ui.Ink, label);
            x += w + gap;
        }
        return hover;
    }

    // ---------- a single share ----------

    public static void Ring(string id, float fraction, Vector2 center, float radius, float thickness, Vector4 color, Vector4 track, string label)
    {
        var s = Get(id);
        var dl = ImGui.GetWindowDrawList();
        dl.AddCircle(center, radius, Col(track), 48, thickness);
        var f = Ease(s, 0, Math.Clamp(fraction, 0f, 1f), 0.1f, 6f);
        if (f > 0.002f)
        {
            dl.PathArcTo(center, radius, -MathF.PI / 2f, -MathF.PI / 2f + MathF.PI * 2f * f, 48);
            dl.PathStroke(Col(color), thickness);
        }
        Text(new Vector2(center.X - TextWidth(label) / 2, center.Y - ImGui.GetTextLineHeight() / 2), Ui.Cream, label);
    }

    // ---------- a year of days ----------

    public static Vector2 HeatmapSize(int count, float cell, float gap) => new((count + 6) / 7 * (cell + gap), 7 * (cell + gap));

    /// <summary>
    /// A square per day in week columns starting on a Monday, brighter the busier the day. The newest square
    /// is outlined, and glows while Gleam is working. Returns the day under the pointer, or -1.
    /// </summary>
    public static int Heatmap(string id, IReadOnlyList<int> values, Vector2 pos, float cell, float gap, bool pulseLast)
    {
        var s = Get(id);
        var dl = ImGui.GetWindowDrawList();
        var peak = 1;
        for (var i = 0; i < values.Count; i++) peak = Math.Max(peak, values[i]);
        var pitch = cell + gap;

        // The day under the pointer, worked out once from where the pointer is rather than by asking each of
        // three hundred and sixty-five squares. The gaps between squares belong to no day, as before.
        var hover = -1;
        if (values.Count > 0 && ImGui.IsMouseHoveringRect(pos, pos + HeatmapSize(values.Count, cell, gap), true))
        {
            var local = ImGui.GetIO().MousePos - pos;
            var col = (int)MathF.Floor(local.X / pitch);
            var row = (int)MathF.Floor(local.Y / pitch);
            var day = col * 7 + row;
            if (col >= 0 && row is >= 0 and < 7 && day < values.Count && local.X - col * pitch < cell && local.Y - row * pitch < cell) hover = day;
        }

        // Five shades, each made into a colour once. Only while the year is still fading in does a square
        // need its own.
        var since = Since(s);
        var settled = Ui.Reduced || since - (values.Count - 1) / 7 * 0.008f >= 0.25f;
        Span<uint> shades = stackalloc uint[5];
        for (var k = 0; k < shades.Length; k++) shades[k] = Col(Shade(k));
        for (var i = 0; i < values.Count; i++)
        {
            var min = pos + new Vector2(i / 7 * pitch, i % 7 * pitch);
            var max = min + new Vector2(cell);
            var v = values[i];
            var share = (float)v / peak;
            var shade = v == 0 ? 0 : share < 0.25f ? 1 : share < 0.5f ? 2 : share < 0.75f ? 3 : 4;
            var color = settled ? shades[shade] : Col(Fade(Shade(shade), Math.Clamp((since - i / 7 * 0.008f) / 0.25f, 0f, 1f)));
            // Square corners: at five to thirteen pixels a rounded corner is barely seen and costs twice the vertices.
            dl.AddRectFilled(min, max, color, 0f);
            if (i == values.Count - 1)
            {
                var a = pulseLast && !Ui.Reduced ? 0.35f + 0.65f * (0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * 4f)) : 0.85f;
                dl.AddRect(min - new Vector2(1.5f * Ui.Scale), max + new Vector2(1.5f * Ui.Scale), Col(Fade(Ui.Cream, a)), 3f * Ui.Scale);
            }
        }
        return hover;
    }

    /// <summary>The heatmap's five shades, from an empty day to the busiest.</summary>
    private static Vector4 Shade(int shade) => shade switch
    {
        0 => new Vector4(1, 1, 1, 0.05f),
        1 => Fade(Ui.Accent, 0.4f),
        2 => Fade(Ui.Accent, 0.68f),
        3 => Ui.Mix(Ui.Accent, Ui.AccentSoft, 0.5f),
        _ => Ui.AccentSoft,
    };
}
