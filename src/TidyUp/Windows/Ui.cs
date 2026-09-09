using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using TidyUp.Core.Model;

namespace TidyUp.Windows;

/// <summary>
/// The plugin's visual language in one place: a deep-ink ground, one gold accent, semantic colours
/// only for actions and states, generous rounding, and a handful of shared primitives (chips, pills,
/// cards, the header) so every window looks like the same product.
/// </summary>
internal static class Ui
{
    // Palette. Ink tones carry a faint blue bias so greys read as chosen rather than default.
    // Gleam: deep indigo ink, one violet (#654FF0), lavender for titles, cool off-white text. Gold is kept for
    // gil alone so money reads as money; the semantic colours stay clear of the accent.
    public static readonly Vector4 Accent = new(0.396f, 0.310f, 0.941f, 1f);      // #654FF0
    public static readonly Vector4 AccentSoft = new(0.663f, 0.612f, 1.0f, 1f);    // #A99CFF
    public static readonly Vector4 Ink = new(0.078f, 0.067f, 0.165f, 0.985f);      // #14112A
    public static readonly Vector4 InkRaised = new(0.114f, 0.098f, 0.220f, 1f);   // #1D1938
    public static readonly Vector4 InkPopup = new(0.106f, 0.094f, 0.204f, 1f);    // #1B1834
    public static readonly Vector4 InkLine = new(1f, 1f, 1f, 0.08f);
    public static readonly Vector4 InkEdge = new(1f, 1f, 1f, 0.16f);
    public static readonly Vector4 Cream = new(0.945f, 0.933f, 1.0f, 1f);         // #F1EEFF
    public static readonly Vector4 Danger = new(0.95f, 0.43f, 0.49f, 1f);         // #F26D7D
    public static readonly Vector4 Ok = new(0.49f, 0.83f, 0.60f, 1f);             // #7ED39A
    public static readonly Vector4 Warn = new(0.95f, 0.70f, 0.42f, 1f);           // #F2B26A
    public static readonly Vector4 Info = new(0.56f, 0.73f, 0.92f, 1f);           // #8FB9EA
    public static readonly Vector4 Muted = new(0.604f, 0.576f, 0.722f, 1f);       // #9A93B8
    public static readonly Vector4 Market = new(0.906f, 0.780f, 0.471f, 1f);      // #E7C778, gil
    public static readonly Vector4 OnAccent = new(1f, 1f, 1f, 1f);

    public static float Scale => ImGuiHelpers.GlobalScale;
    public static float Space => 8f * Scale;
    public static float Rounding => 6f * Scale;

    // ---------- window-wide style ----------

    /// <summary>Pushed by every Gleam window around Begin/End. Rounded corners, calmer frames, ink ground.</summary>
    public static IDisposable PushWindowStyle() => new WindowStyle();

    private sealed class WindowStyle : IDisposable
    {
        private readonly IDisposable style;
        private readonly IDisposable color;

        public WindowStyle()
        {
            style = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 9f * Scale)
                .Push(ImGuiStyleVar.ChildRounding, Rounding)
                .Push(ImGuiStyleVar.FrameRounding, 5f * Scale)
                .Push(ImGuiStyleVar.PopupRounding, Rounding)
                .Push(ImGuiStyleVar.GrabRounding, 4f * Scale)
                .Push(ImGuiStyleVar.ScrollbarRounding, 6f * Scale)
                .Push(ImGuiStyleVar.ScrollbarSize, 10f * Scale)
                .Push(ImGuiStyleVar.WindowPadding, new Vector2(14f * Scale, 12f * Scale))
                .Push(ImGuiStyleVar.FramePadding, new Vector2(8f * Scale, 4f * Scale))
                .Push(ImGuiStyleVar.ItemSpacing, new Vector2(8f * Scale, 6f * Scale))
                .Push(ImGuiStyleVar.CellPadding, new Vector2(6f * Scale, 3f * Scale))
                .Push(ImGuiStyleVar.WindowBorderSize, 1f)
                .Push(ImGuiStyleVar.PopupBorderSize, 1f)
                // ImGui reads this as a fraction of the space left over after the title text, not as a
                // fixed inset, so any value above zero pushes the title further right the wider the window
                // gets. The title belongs hard against the collapse arrow at every size.
                .Push(ImGuiStyleVar.WindowTitleAlign, new Vector2(0f, 0.5f));

            color = ImRaii.PushColor(ImGuiCol.WindowBg, Ink)
                .Push(ImGuiCol.ChildBg, Vector4.Zero)
                .Push(ImGuiCol.PopupBg, InkPopup)
                .Push(ImGuiCol.Border, InkEdge)
                .Push(ImGuiCol.TitleBg, new Vector4(0.06f, 0.05f, 0.125f, 1f))
                .Push(ImGuiCol.TitleBgActive, new Vector4(0.095f, 0.08f, 0.19f, 1f))
                .Push(ImGuiCol.TitleBgCollapsed, new Vector4(0.06f, 0.05f, 0.125f, 0.8f))
                .Push(ImGuiCol.FrameBg, new Vector4(1f, 1f, 1f, 0.055f))
                .Push(ImGuiCol.FrameBgHovered, new Vector4(1f, 1f, 1f, 0.09f))
                .Push(ImGuiCol.FrameBgActive, new Vector4(1f, 1f, 1f, 0.12f))
                .Push(ImGuiCol.Button, new Vector4(1f, 1f, 1f, 0.07f))
                .Push(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.12f))
                .Push(ImGuiCol.ButtonActive, new Vector4(1f, 1f, 1f, 0.16f))
                .Push(ImGuiCol.Header, Accent * new Vector4(1, 1, 1, 0.14f))
                .Push(ImGuiCol.HeaderHovered, Accent * new Vector4(1, 1, 1, 0.20f))
                .Push(ImGuiCol.HeaderActive, Accent * new Vector4(1, 1, 1, 0.26f))
                .Push(ImGuiCol.CheckMark, Accent)
                .Push(ImGuiCol.SliderGrab, Accent * new Vector4(1, 1, 1, 0.85f))
                .Push(ImGuiCol.SliderGrabActive, Accent)
                .Push(ImGuiCol.Separator, InkLine)
                .Push(ImGuiCol.ScrollbarBg, Vector4.Zero)
                .Push(ImGuiCol.ScrollbarGrab, new Vector4(1f, 1f, 1f, 0.12f))
                .Push(ImGuiCol.ScrollbarGrabHovered, new Vector4(1f, 1f, 1f, 0.2f))
                .Push(ImGuiCol.TableRowBg, Vector4.Zero)
                .Push(ImGuiCol.TableRowBgAlt, new Vector4(1f, 1f, 1f, 0.025f))
                .Push(ImGuiCol.TableBorderLight, Vector4.Zero)
                .Push(ImGuiCol.TableBorderStrong, Vector4.Zero)
                .Push(ImGuiCol.TextSelectedBg, Accent * new Vector4(1, 1, 1, 0.3f))
                .Push(ImGuiCol.PlotHistogram, Accent)
                .Push(ImGuiCol.ResizeGrip, Vector4.Zero)
                .Push(ImGuiCol.ResizeGripHovered, Accent * new Vector4(1, 1, 1, 0.4f))
                .Push(ImGuiCol.ResizeGripActive, Accent * new Vector4(1, 1, 1, 0.7f))
                .Push(ImGuiCol.TextDisabled, Muted * new Vector4(1, 1, 1, 0.8f));
        }

        public void Dispose()
        {
            color.Dispose();
            style.Dispose();
        }
    }

    // ---------- text ----------

    public static void Text(string text) => ImGui.TextUnformatted(text);

    public static void TextColored(Vector4 color, string text)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
    }

    /// <summary>Coloured text that wraps, for a sentence long enough to need the room.</summary>
    public static void TextColoredWrapped(Vector4 color, string text)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
    }

    /// <summary>Secondary text: explanations, counts, anything the eye should skip on a first pass.</summary>
    public static void Hint(string text) => ImGui.TextDisabled(text);

    public static void HintWrapped(string text)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Text, Muted);
        ImGui.TextWrapped(text);
    }

    /// <summary>A quiet section label with breathing room above it: small caps feel via letter-spaced uppercase.</summary>
    public static void Section(string title)
    {
        ImGui.Dummy(new Vector2(0, Space));
        TextColored(Muted, title.ToUpperInvariant());
        ImGui.Spacing();
    }

    /// <summary>
    /// Titles a group of settings: the decision as a question, then one line saying what it changes. Every
    /// group is titled this way, so a settings page reads as a short interview rather than a control panel.
    /// </summary>
    public static void Ask(string question, string? explain = null)
    {
        TextColored(AccentSoft, question);
        if (!string.IsNullOrEmpty(explain)) HintWrapped(explain);
        Gap(0.25f);
    }

    /// <summary>Width a checkbox with this label occupies, for laying out a wrapping row of them.</summary>
    public static float CheckWidth(string label)
    {
        var cut = label.IndexOf("##", StringComparison.Ordinal);
        if (cut >= 0) label = label[..cut];
        return ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label, false, 0).X;
    }

    /// <summary>A Font Awesome glyph inline with text, in the given colour.</summary>
    public static void Icon(FontAwesomeIcon icon, Vector4? color = null)
    {
        using var f = ImRaii.PushFont(UiBuilder.IconFont);
        using var c = ImRaii.PushColor(ImGuiCol.Text, color ?? Muted);
        ImGui.TextUnformatted(icon.ToIconString());
    }

    public static float IconWidth(FontAwesomeIcon icon)
    {
        using var f = ImRaii.PushFont(UiBuilder.IconFont);
        return ImGui.CalcTextSize(icon.ToIconString(), false, 0).X;
    }

    public static void Gap(float multiple = 1f) => ImGui.Dummy(new Vector2(0, Space * multiple));

    /// <summary>A label in the muted tone followed by its value on the same line, label column fixed.</summary>
    public static void KeyValue(string label, string value, Vector4? valueColor = null, float labelWidth = 92f)
    {
        var x = ImGui.GetCursorPosX();
        Hint(label);
        ImGui.SameLine();
        ImGui.SetCursorPosX(x + labelWidth * Scale);
        if (valueColor is { } c) TextColored(c, value); else Text(value);
    }

    /// <summary>A tooltip with the plugin's padding and a fixed comfortable width. Dispose to close.</summary>
    // ---------- motion ----------
    // Small, frame-rate independent eases keyed by a string. Everything that moves in the plugin goes through
    // these three, so the feel is the same everywhere: quick to react, soft to settle.

    /// <summary>
    /// When set, everything that would ease instead arrives at its end state on the first frame. Decorative
    /// motion with no end state -- bobbing, breathing, pulsing -- stops altogether. Spinners and progress
    /// keep moving, because there they are the information rather than the decoration.
    /// </summary>
    public static bool Reduced { get; set; }

    private static readonly Dictionary<string, float> motion = new();
    private static readonly Dictionary<string, (double First, double Last)> appear = new();
    private static readonly Dictionary<string, bool> hoverLast = new();

    /// <summary>A value that follows <paramref name="target"/> with an exponential ease; higher speed settles sooner.</summary>
    public static float Smooth(string id, float target, float speed = 12f)
    {
        if (Reduced) { motion[id] = target; return target; }
        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        if (!motion.TryGetValue(id, out var v)) v = target;
        v += (target - v) * (1f - MathF.Exp(-speed * dt));
        if (MathF.Abs(v - target) < 0.001f) v = target;
        motion[id] = v;
        return v;
    }

    /// <summary>0 → 1 over the first moments something is on screen; starts over once it has been away for a bit.</summary>
    public static float Appear(string id, float seconds = 0.22f)
    {
        if (Reduced) return 1f;
        var now = ImGui.GetTime();
        if (!appear.TryGetValue(id, out var t) || now - t.Last > 0.3) t = (now, now);
        appear[id] = (t.First, now);
        return EaseOut((float)Math.Clamp((now - t.First) / seconds, 0, 1));
    }

    /// <summary>Seeds an eased value, so the next <see cref="Smooth"/> starts there and settles from it.</summary>
    public static void SetMotion(string id, float value) { if (!Reduced) motion[id] = value; }

    public static float EaseOut(float t) => 1f - (1f - t) * (1f - t);

    /// <summary>Hover state of the last frame, eased. Call <see cref="RecordHover"/> right after the item.</summary>
    public static float Hover(string id) => Smooth("hover:" + id, hoverLast.GetValueOrDefault(id) ? 1f : 0f, 16f);
    public static void RecordHover(string id) => hoverLast[id] = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);

    public static Vector4 Mix(Vector4 a, Vector4 b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    private static readonly Dictionary<string, double> counts = new();

    /// <summary>A number that counts to its target instead of jumping. Doubles, so gil totals stay exact.</summary>
    public static long Count(string id, long target, float speed = 9f)
    {
        if (Reduced) { counts[id] = target; return target; }
        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        if (!counts.TryGetValue(id, out var v)) v = target;
        v += (target - v) * (1 - Math.Exp(-speed * dt));
        if (Math.Abs(target - v) < 0.5) v = target;
        counts[id] = v;
        return (long)Math.Round(v);
    }

    private static object? pageKey;
    private static double pageAt;

    /// <summary>Switching page fades the new one up and settles it a few pixels, so the change reads as movement.</summary>
    public static IDisposable PageTransition(object key)
    {
        if (!Equals(pageKey, key)) { pageKey = key; pageAt = ImGui.GetTime(); }
        if (Reduced) return ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha);
        var t = (float)Math.Clamp((ImGui.GetTime() - pageAt) / 0.20, 0, 1);
        var e = EaseOut(t);
        if (t < 1f) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (1f - e) * 10f * Scale);
        return ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * (0.25f + 0.75f * e));
    }

    /// <summary>
    /// The fade a section's contents get when it opens. Every collapsing thing in the plugin uses this, so
    /// opening a settings fold reads the same as opening a group in the list.
    /// </summary>
    public static IDisposable FoldFade(string key)
    {
        var a = Appear("fold:" + key, 0.16f);
        if (a < 1f) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (1f - a) * 6f * Scale);
        return ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * a);
    }

    /// <summary>A collapsing section whose contents fade and settle in. The body is indented under its title.</summary>
    public static void Fold(string title, Action body, float indent = 12f)
    {
        using var id = ImRaii.PushId(title);
        if (!ImGui.CollapsingHeader(title, ImGuiTreeNodeFlags.None)) return;
        using var fade = FoldFade(title);
        using var pad = ImRaii.PushIndent(indent, true, true);
        Gap(0.3f);
        body();
        Gap(0.5f);
    }

    // A row that has been asked to go keeps its place for a moment and shrinks out of it, so the rows below
    // slide up instead of jumping. The caller defers the real removal until Leaving reports it is finished.
    private static readonly Dictionary<string, double> leaving = new();

    /// <summary>Starts a row's exit. Nothing is removed yet; keep drawing it until <see cref="Leaving"/> returns 0.</summary>
    public static void Leave(string key) => leaving[key] = ImGui.GetTime();

    public static bool IsLeaving(string key) => leaving.ContainsKey(key);

    /// <summary>
    /// How much of a leaving row is left, 1 down to 0. Returns 0 exactly once, on the frame the caller should
    /// do the real removal; anything not leaving is 1, so this is safe to call on every row.
    /// </summary>
    public static float Leaving(string key, float seconds = 0.16f)
    {
        if (!leaving.TryGetValue(key, out var at)) return 1f;
        if (Reduced) { leaving.Remove(key); return 0f; }
        var left = 1f - (float)Math.Clamp((ImGui.GetTime() - at) / seconds, 0, 1);
        if (left <= 0f) { leaving.Remove(key); return 0f; }
        return left;
    }

    /// <summary>Pushes the alpha and squeezes the row height for something on its way out.</summary>
    public static IDisposable LeavingScope(float left) =>
        ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * left)
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, ImGui.GetStyle().ItemSpacing.Y * left));

    private static readonly Dictionary<string, (string Text, double At)> swaps = new();

    /// <summary>
    /// A line of text that changes while you are reading it. The new wording fades up from just below the
    /// old, so a status line that rewrites itself several times a second stays legible instead of flickering.
    /// </summary>
    public static void TextSwap(string id, string text, Vector4? color = null)
    {
        var now = ImGui.GetTime();
        if (!swaps.TryGetValue(id, out var last) || last.Text != text) swaps[id] = last = (text, now);
        var a = Reduced ? 1f : EaseOut((float)Math.Clamp((now - last.At) / 0.18, 0, 1));
        var y = ImGui.GetCursorPosY();
        if (a < 1f) ImGui.SetCursorPosY(y + (1f - a) * 4f * Scale);
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * (0.25f + 0.75f * a)))
        {
            if (color is { } c) TextColored(c, text); else Text(text);
        }
        if (a < 1f) ImGui.SetCursorPosY(ImGui.GetCursorPosY() - (1f - a) * 4f * Scale);
    }

    /// <summary>
    /// A tick box that fills and draws its check on, rather than flipping between two pictures. Anything
    /// before "##" is drawn as a label and is part of the click target, so this stands in for a plain checkbox.
    /// </summary>
    public static bool Check(string id, ref bool value, bool disabled = false)
    {
        var visible = id.Split("##")[0];
        var h = ImGui.GetFrameHeight();
        var size = Math.Min(h, 19f * Scale);
        var gap = visible.Length == 0 ? 0f : 8f * Scale;
        var textW = visible.Length == 0 ? 0f : ImGui.CalcTextSize(visible, false, 0).X;
        var start = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(size + gap + textW, h)) && !disabled;
        if (clicked) value = !value;
        var key = $"chk:{ImGui.GetID(id)}";
        RecordHover(key);
        var hv = disabled ? 0f : Hover(key);
        var on = Smooth(key + ":on", value ? 1f : 0f, 18f);
        var pos = start + new Vector2(0, (h - size) / 2);
        var dl = ImGui.GetWindowDrawList();
        var r = 5f * Scale;
        var dim = disabled ? 0.45f : 1f;
        dl.AddRectFilled(pos, pos + new Vector2(size, size), ImGui.GetColorU32(Mix(new Vector4(1, 1, 1, (0.06f + 0.06f * hv) * dim), Accent * new Vector4(1, 1, 1, dim), on)), r);
        // The border fades out as the box fills: an outline over a solid fill is what makes the edge look ragged.
        dl.AddRect(pos, pos + new Vector2(size, size), ImGui.GetColorU32(Mix(InkEdge * new Vector4(1, 1, 1, dim), Accent * new Vector4(1, 1, 1, 0f), on)), r, ImDrawFlags.None, 1f * Scale);
        if (on > 0.01f)
        {
            // The short leg draws first, then the long one: a tick being made, not a tick appearing.
            // Plain lines meet in a hard notch and end in square edges, so each end and the corner gets a
            // small filled circle. Those are anti-aliased, which is what keeps the tick smooth at this size.
            var p1 = pos + new Vector2(size * 0.26f, size * 0.53f);
            var p2 = pos + new Vector2(size * 0.44f, size * 0.71f);
            var p3 = pos + new Vector2(size * 0.77f, size * 0.30f);
            var col = ImGui.GetColorU32(OnAccent * new Vector4(1, 1, 1, on * dim));
            var w = Math.Max(2f, size * 0.135f);
            var cap = w / 2f;
            var a = Math.Clamp(on * 2f, 0f, 1f);
            var end1 = p1 + (p2 - p1) * a;
            dl.AddLine(p1, end1, col, w);
            dl.AddCircleFilled(p1, cap, col, 12);
            dl.AddCircleFilled(end1, cap, col, 12);
            var b = Math.Clamp(on * 2f - 1f, 0f, 1f);
            if (b > 0)
            {
                var end2 = p2 + (p3 - p2) * b;
                dl.AddLine(p2, end2, col, w);
                dl.AddCircleFilled(end2, cap, col, 12);
            }
        }
        if (visible.Length > 0)
        {
            var text = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
            dl.AddText(new Vector2(start.X + size + gap, start.Y + (h - ImGui.GetTextLineHeight()) / 2),
                ImGui.GetColorU32(Mix(text, text * new Vector4(1, 1, 1, 0.45f), disabled ? 1f : 0f)), visible);
        }
        return clicked;
    }

    public static IDisposable RichTooltip(float width = 340f)
    {
        var min = ImGui.GetItemRectMin();
        var style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14f * Scale, 12f * Scale))
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(8f * Scale, 5f * Scale))
            .Push(ImGuiStyleVar.WindowRounding, 8f * Scale)
            .Push(ImGuiStyleVar.Alpha, Appear($"rtip:{min.X:F0},{min.Y:F0}", 0.14f));
        ImGui.SetNextWindowSize(new Vector2(width * Scale, 0));
        ImGui.BeginTooltip();
        return new TooltipScope(style);
    }

    private sealed class TooltipScope(IDisposable style) : IDisposable
    {
        public void Dispose()
        {
            ImGui.EndTooltip();
            style.Dispose();
        }
    }

    /// <summary>A hairline across the content width. Quieter than ImGui.Separator.</summary>
    public static void Rule()
    {
        var p = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddLine(p, new Vector2(p.X + w, p.Y), ImGui.GetColorU32(InkLine), 1f);
        ImGui.Dummy(new Vector2(w, 1f));
    }

    // ---------- header ----------

    /// <summary>
    /// Product header: logo tile, name in gold, one muted line under it. An optional control of the given
    /// width is drawn right-aligned and vertically centred on the same row.
    /// </summary>
    /// <summary>Everywhere the one window can be. Clean and Organize are the two things it does; the other two are references.</summary>
    public enum AppMode { Clean, Organize, History, Settings }

    public static readonly IReadOnlyList<(AppMode, string)> ModeOptions = [(AppMode.Clean, "Clean"), (AppMode.Organize, "Organize")];

    /// <summary>The Clean | Organize switch. Returns true when the other mode was picked.</summary>
    public static bool ModeSwitch(AppMode current)
    {
        var mode = current;
        var changed = Segmented("##mode", ref mode, ModeOptions) && mode != current;
        Tooltip("Clean gets rid of junk. Organize puts what you keep where you want it.");
        return changed;
    }

    /// <summary>A quiet way back to the list, for the pages that are not the list.</summary>
    public static bool BackLink(string label = "Back to the list")
    {
        var clicked = LinkButton($"◂  {label}");
        Tooltip("Returns to what Gleam found.");
        return clicked;
    }

    /// <summary>Width reserved for the title and subtitle, so anything pinned after them never moves.</summary>
    private const float TitleColumn = 300f;

    /// <summary>Cuts text to fit, with an ellipsis, rather than letting it push its neighbours along.</summary>
    private static string Clip(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || ImGui.CalcTextSize(text, false, 0).X <= maxWidth) return text;
        var cut = text;
        while (cut.Length > 1 && ImGui.CalcTextSize(cut + "…", false, 0).X > maxWidth) cut = cut[..^1];
        return cut + "…";
    }

    public static void Header(ImTextureID logo, string title, string subtitle, float rightWidth = 0f, Action? right = null, string? rightNote = null, Action? afterTitle = null, float afterTitleWidth = 0f)
    {
        var size = 36f * Scale;
        var start = ImGui.GetCursorPos();
        var logoW = logo.IsNull ? 0f : size + 10f * Scale;
        // The pinned control keeps one column, but a narrow window wins: it slides left rather than
        // running under whatever sits on the right, and the title clips to whatever room is left.
        var pinnedW = afterTitleWidth > 0 ? afterTitleWidth : 170f * Scale;
        var pad = ImGui.GetStyle().WindowPadding.X;
        var rightEdge = ImGui.GetWindowWidth() - pad - (rightWidth > 0 ? rightWidth + 16f * Scale : 0f);
        var room = rightEdge - pinnedW - start.X - logoW;
        // Beside the title when there is room for both it and a readable title; on its own line below when not.
        var pinnedBeside = afterTitle is not null && room >= 140f * Scale;
        var column = Math.Clamp(room, 140f * Scale, TitleColumn * Scale);
        var rightBlock = right is null ? 0f : ImGui.GetFrameHeight() + (rightNote is null ? 0f : ImGui.GetTextLineHeight() + 3f * Scale);
        var rowH = Math.Max(size, rightBlock);
        if (!logo.IsNull)
        {
            ImageRounded(logo, new Vector2(size, size), 8f * Scale);
            ImGui.SameLine(0, 10f * Scale);
        }
        var lineH = ImGui.GetTextLineHeight();
        var block = lineH * 2 + 2f * Scale;
        ImGui.SetCursorPosY(start.Y + Math.Max(0, (size - block) / 2));
        using (ImRaii.Group())
        {
            // With something pinned beside it, the title block keeps to its column rather than pushing it along.
            var titleRoom = pinnedBeside ? column - 12f * Scale : float.MaxValue;
            TextColored(AccentSoft, Clip(title, titleRoom));
            Hint(Clip(subtitle, titleRoom));
        }
        if (pinnedBeside)
        {
            // One column, so switching page never moves the switch you just pressed.
            ImGui.SameLine();
            ImGui.SetCursorPos(new Vector2(start.X + logoW + column, start.Y + Math.Max(0, (size - ImGui.GetFrameHeight()) / 2)));
            afterTitle!();
        }
        if (right is not null)
        {
            ImGui.SameLine();
            RightAlign(rightWidth);
            var top = start.Y + Math.Max(0, (rowH - rightBlock) / 2);
            ImGui.SetCursorPosY(top);
            right();
            if (rightNote is not null)
            {
                var noteW = ImGui.CalcTextSize(rightNote, false, 0).X;
                ImGui.SetCursorPos(new Vector2(ImGui.GetWindowWidth() - noteW - ImGui.GetStyle().WindowPadding.X, top + ImGui.GetFrameHeight() + 3f * Scale));
                TextColored(Accent * new Vector4(1, 1, 1, 0.9f), rightNote);
            }
        }
        ImGui.SetCursorPos(new Vector2(start.X, start.Y + rowH + 4f * Scale));
        if (afterTitle is not null && !pinnedBeside)
        {
            afterTitle();
            Gap(0.3f);
        }
        ImGui.Dummy(Vector2.Zero);
    }

    // ---------- buttons ----------

    public static bool Button(string label, float width = 0f) => ImGui.Button(label, new Vector2(width, 0f));

    /// <summary>A turning arc. Says "working" without pretending to know how far along it is.</summary>
    public static void Spinner(Vector2 centre, float radius, float thickness, Vector4 colour, float speed = 2.6f)
    {
        var dl = ImGui.GetWindowDrawList();
        var t = (float)ImGui.GetTime() * speed;
        // The gap breathes a little, so the arc reads as chasing its own tail rather than turning stiffly.
        var sweep = 3.6f + 1.4f * MathF.Sin(t * 0.9f);
        dl.PathArcTo(centre, radius, t, t + sweep, 32);
        dl.PathStroke(ImGui.GetColorU32(colour), ImDrawFlags.None, thickness);
    }

    /// <summary>A button with a leading icon. While busy the icon becomes a turning arc.</summary>
    public static bool IconButton(FontAwesomeIcon icon, string label, float width = 0f, bool busy = false)
    {
        var id = $"{label}##{icon}";
        var pad = ImGui.GetStyle().FramePadding;
        var textW = ImGui.CalcTextSize(label, false, 0).X;
        var iconW = IconWidth(icon);
        var w = width > 0 ? width : textW + iconW + pad.X * 2 + 6f * Scale;
        var pos = ImGui.GetCursorScreenPos();
        var key = $"icon:{ImGui.GetID(id)}";
        var hv = Hover(key);
        bool clicked;
        using (ImRaii.PushColor(ImGuiCol.Button, Mix(ImGui.GetStyle().Colors[(int)ImGuiCol.Button], ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered], hv)))
            clicked = ImGui.Button($"##{id}", new Vector2(w, 0));
        RecordHover(key);
        var dl = ImGui.GetWindowDrawList();
        var h = ImGui.GetFrameHeight();
        var x = pos.X + (w - (textW + iconW + 6f * Scale)) / 2;
        var y = pos.Y + (h - ImGui.GetTextLineHeight()) / 2;
        var spin = Smooth(key + ":busy", busy ? 1f : 0f, 12f);
        if (spin < 0.99f)
            using (ImRaii.PushFont(UiBuilder.IconFont))
                dl.AddText(new Vector2(x, y), ImGui.GetColorU32(Mix(Muted, AccentSoft, hv) * new Vector4(1, 1, 1, 1f - spin)), icon.ToIconString());
        if (spin > 0.01f)
            Spinner(new Vector2(x + iconW / 2, pos.Y + h / 2), iconW * 0.42f, 2f * Scale, AccentSoft * new Vector4(1, 1, 1, spin));
        dl.AddText(new Vector2(x + iconW + 6f * Scale, y), ImGui.GetColorU32(ImGuiCol.Text), label);
        return clicked;
    }

    /// <summary>A small square button showing only a glyph; the tooltip carries the words.</summary>
    public static bool GlyphButton(FontAwesomeIcon icon, string id, string tooltip, Vector4? color = null)
    {
        var h = ImGui.GetFrameHeight();
        bool clicked;
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(1, 1, 1, 0.08f)))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(1, 1, 1, 0.12f)))
        using (ImRaii.PushColor(ImGuiCol.Text, color ?? Muted))
        using (ImRaii.PushFont(UiBuilder.IconFont))
            clicked = ImGui.Button($"{icon.ToIconString()}##{id}", new Vector2(h, h));
        Tooltip(tooltip);
        return clicked;
    }

    /// <summary>The one filled button on a screen: gold, rounded, with a soft sheen. Danger turns it red.</summary>
    public static bool PrimaryButton(string label, float width = 0f, bool danger = false)
    {
        var color = danger ? Danger : Accent;
        var textW = ImGui.CalcTextSize(label, false, 0).X;
        var pad = ImGui.GetStyle().FramePadding;
        var w = width > 0 ? width : textW + pad.X * 4;
        var h = ImGui.GetFrameHeight() + 2f * Scale;
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##primary{label}", new Vector2(w, h));
        var key = $"primary:{ImGui.GetID($"##primary{label}")}";
        RecordHover(key);
        var hv = Hover(key);
        var held = ImGui.IsItemActive();
        var disabled = ImGui.GetStyle().Alpha < 0.99f;
        var dl = ImGui.GetWindowDrawList();
        var alpha = disabled ? 0.45f : held ? 1f : 0.85f + 0.10f * hv;
        var fill = color * new Vector4(1, 1, 1, alpha);
        var r = Rounding;
        var press = Smooth($"press:{ImGui.GetID($"##primary{label}")}", held ? 1f : 0f, 26f);
        pos += new Vector2(0, press * 1.5f * Scale);
        h -= press * 1.5f * Scale;
        if (!disabled)
        {
            // A halo that swells on hover; danger buttons breathe a little so "Stop" is easy to find.
            var breathe = danger && !Reduced ? 0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * 2.4f) : 0f;
            var halo = 0.10f * hv + 0.07f * breathe;
            var grow = 3f * Scale + 3f * Scale * hv;
            if (halo > 0.005f) dl.AddRectFilled(pos - new Vector2(grow, grow), pos + new Vector2(w + grow, h + grow), ImGui.GetColorU32(color * new Vector4(1, 1, 1, halo)), r + grow);
        }
        dl.AddRectFilled(pos, pos + new Vector2(w, h), ImGui.GetColorU32(fill), r);
        // sheen on the upper half, a shade line on the bottom edge
        dl.AddRectFilled(pos, pos + new Vector2(w, h * 0.5f), ImGui.GetColorU32(new Vector4(1, 1, 1, disabled ? 0.04f : 0.10f)), r, ImDrawFlags.RoundCornersTop);
        dl.AddRect(pos, pos + new Vector2(w, h), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.25f)), r);
        var tp = pos + new Vector2((w - textW) / 2, (h - ImGui.GetTextLineHeight()) / 2);
        dl.AddText(tp, ImGui.GetColorU32(OnAccent * new Vector4(1, 1, 1, disabled ? 0.7f : 1f)), label);
        return clicked && !disabled;
    }

    /// <summary>A text-only button for secondary actions.</summary>
    public static bool LinkButton(string label)
    {
        var key = $"link:{ImGui.GetID(label)}";
        var hv = Hover(key);
        using var b = ImRaii.PushColor(ImGuiCol.Button, new Vector4(1, 1, 1, 0.08f * hv));
        using var h = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(1, 1, 1, 0.08f));
        using var a = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(1, 1, 1, 0.12f));
        using var t = ImRaii.PushColor(ImGuiCol.Text, Mix(Muted, Cream, hv));
        var clicked = ImGui.Button(label, new Vector2(0, 0));
        RecordHover(key);
        return clicked;
    }

    /// <summary>Small coloured status chip, e.g. "ready" or "needs saddlebag".</summary>
    /// <param name="id">
    /// Give a pill that changes state -- installed to missing, ready to busy -- a stable id and its colour
    /// eases between the two instead of flipping, so the change is something you see rather than something
    /// you notice afterwards.
    /// </param>
    public static void Pill(string text, Vector4 color, FontAwesomeIcon? icon = null, string? id = null)
    {
        if (id is not null) color = PillColor(id, color);
        using var b = ImRaii.PushColor(ImGuiCol.Button, color * new Vector4(1, 1, 1, 0.16f));
        using var h = ImRaii.PushColor(ImGuiCol.ButtonHovered, color * new Vector4(1, 1, 1, 0.16f));
        using var a = ImRaii.PushColor(ImGuiCol.ButtonActive, color * new Vector4(1, 1, 1, 0.16f));
        using var t = ImRaii.PushColor(ImGuiCol.Text, color);
        using var r = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 10f * Scale);
        using var p = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(8f * Scale, 1f * Scale));
        if (icon is null)
        {
            ImGui.Button(text, new Vector2(0, 0));
            return;
        }
        var iconW = IconWidth(icon.Value);
        var textW = ImGui.CalcTextSize(text, false, 0).X;
        var w = iconW + textW + 8f * Scale * 2 + 5f * Scale;
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Button($"##pill{text}", new Vector2(w, 0));
        var dl = ImGui.GetWindowDrawList();
        var y = pos.Y + (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) / 2 - 1f * Scale;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            dl.AddText(new Vector2(pos.X + 8f * Scale, y + 1f * Scale), ImGui.GetColorU32(color), icon.Value.ToIconString());
        dl.AddText(new Vector2(pos.X + 8f * Scale + iconW + 5f * Scale, y + 1f * Scale), ImGui.GetColorU32(color), text);
    }

    private static readonly Dictionary<string, Vector4> pillColors = new();

    private static Vector4 PillColor(string id, Vector4 target)
    {
        if (Reduced || !pillColors.TryGetValue(id, out var shown)) return pillColors[id] = target;
        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        return pillColors[id] = Mix(shown, target, 1f - MathF.Exp(-9f * dt));
    }

    /// <summary>Width a pill occupies, for laying out a row that has to reserve room for one.</summary>
    public static float PillWidth(string text, FontAwesomeIcon? icon = null) =>
        ImGui.CalcTextSize(text, false, 0).X + 8f * Scale * 2 + (icon is null ? 0f : IconWidth(icon.Value) + 5f * Scale);

    /// <summary>A toggle chip for filters. Active chips fill with the accent.</summary>
    public static bool Chip(string label, bool active)
    {
        var textW = ImGui.CalcTextSize(label, false, 0).X;
        var h = ImGui.GetTextLineHeight() + 5f * Scale;
        var w = textW + 18f * Scale;
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(label, new Vector2(w, h));
        var key = $"chip:{ImGui.GetID(label)}";
        RecordHover(key);
        var hv = Hover(key);
        var on = Smooth(key + ":on", active ? 1f : 0f, 14f);

        var idle = new Vector4(1, 1, 1, 0.06f + 0.06f * hv);
        var lit = Accent * new Vector4(1, 1, 1, 0.85f + 0.15f * hv);
        var text = Mix(ImGui.GetStyle().Colors[(int)ImGuiCol.Text], OnAccent, on);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(w, h), ImGui.GetColorU32(Mix(idle, lit, on)), h / 2);
        if (on > 0.01f) dl.AddRect(pos, pos + new Vector2(w, h), ImGui.GetColorU32(AccentSoft * new Vector4(1, 1, 1, 0.35f * on)), h / 2);
        dl.AddText(pos + new Vector2(9f * Scale, (h - ImGui.GetTextLineHeight()) / 2), ImGui.GetColorU32(text), label);
        return clicked;
    }

    // ---------- images ----------

    /// <summary>
    /// An icon that grows a touch under the cursor without moving anything. The layout box stays the size it
    /// always was and only the picture is drawn larger, centred on it: growing the box instead would make
    /// every row below the cursor shift down, which reads as the list wobbling as the mouse crosses it.
    /// </summary>
    public static void ImageLifted(ImTextureID tex, float box, float lift, float rounding, string? key = null)
    {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(box, box));
        if (tex.IsNull) return;

        var a = 1f;
        if (key is not null && !Reduced)
        {
            if (!iconArrived.TryGetValue(key, out var at)) iconArrived[key] = at = ImGui.GetTime();
            a = EaseOut((float)Math.Clamp((ImGui.GetTime() - at) / 0.22, 0, 1));
        }
        var grow = box * 0.11f * Math.Clamp(lift, 0f, 1f);
        var min = pos - new Vector2(grow / 2, grow / 2);
        var max = min + new Vector2(box + grow, box + grow);
        var tint = ImGui.GetColorU32(new Vector4(1, 1, 1, ImGui.GetStyle().Alpha * a));
        ImGui.GetWindowDrawList().AddImageRounded(tex, min, max, Vector2.Zero, Vector2.One, tint, rounding);
    }

    private static readonly Dictionary<string, double> iconArrived = new();

    /// <param name="key">
    /// Item icons load off the main thread, so without this they snap in one at a time as a list scrolls.
    /// Pass a stable key and each icon fades up the moment its texture is ready.
    /// </param>
    public static void ImageRounded(ImTextureID tex, Vector2 size, float rounding, string? key = null)
    {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        if (tex.IsNull) return;

        var a = 1f;
        if (key is not null && !Reduced)
        {
            if (!iconArrived.TryGetValue(key, out var at)) iconArrived[key] = at = ImGui.GetTime();
            a = EaseOut((float)Math.Clamp((ImGui.GetTime() - at) / 0.22, 0, 1));
        }
        // Draw-list calls ignore the style alpha, so a fading row would keep its icons at full strength.
        var tint = ImGui.GetColorU32(new Vector4(1, 1, 1, ImGui.GetStyle().Alpha * a));
        ImGui.GetWindowDrawList().AddImageRounded(tex, pos, pos + size, Vector2.Zero, Vector2.One, tint, rounding);
    }

    /// <summary>Faded logo and a line of text, centred. For "nothing here" states.</summary>
    public static void EmptyState(ImTextureID logo, string text, string? hint = null)
    {
        var size = 72f * Scale;
        var avail = ImGui.GetContentRegionAvail();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0, avail.Y * 0.18f));
        var a = Appear("empty:" + text, 0.3f);
        using var alpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, a);
        if (!logo.IsNull)
        {
            var t = Reduced ? 0f : (float)ImGui.GetTime();
            var bob = Reduced ? 0f : MathF.Sin(t * 1.5f) * 3f * Scale;
            ImGui.SetCursorPosX(Math.Max(0, (ImGui.GetWindowWidth() - size) / 2));
            var pos = ImGui.GetCursorScreenPos() + new Vector2(0, bob + 8f * Scale * (1f - a));
            ImGui.Dummy(new Vector2(size, size));
            var dl = ImGui.GetWindowDrawList();
            dl.AddCircleFilled(pos + new Vector2(size / 2, size / 2 + 6f * Scale), size * 0.62f, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.18f * a)));
            dl.AddImageRounded(logo, pos, pos + new Vector2(size, size), Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1, 1, 1, (0.52f + 0.06f * MathF.Sin(t * 1.5f + 1f)) * a)), 14f * Scale);
            Gap(0.6f);
        }
        Centered(text);
        if (hint is not null) Centered(hint, muted: true);
    }

    public static void Centered(string text, bool muted = false)
    {
        var w = ImGui.CalcTextSize(text, false, 0).X;
        ImGui.SetCursorPosX(Math.Max(0, (ImGui.GetWindowWidth() - w) / 2));
        if (muted) Hint(text); else Text(text);
    }

    /// <summary>Centred text that cross-fades when its wording changes. See <see cref="TextSwap"/>.</summary>
    public static void CenteredSwap(string id, string text, bool muted = false)
    {
        var w = ImGui.CalcTextSize(text, false, 0).X;
        ImGui.SetCursorPosX(Math.Max(0, (ImGui.GetWindowWidth() - w) / 2));
        TextSwap(id, text, muted ? Muted * new Vector4(1, 1, 1, 0.8f) : null);
    }

    /// <summary>Slim accent progress bar with the label drawn to its right.</summary>
    private static readonly Dictionary<string, (float Shown, double At)> progressAnim = new();

    /// <summary>
    /// The run bar. With a fraction it fills smoothly towards the target with a soft glow at the leading edge
    /// and a sheen drifting across; without one (nothing to count yet, travelling) a light pill sweeps the track.
    /// The label sits under the bar.
    /// </summary>
    private static readonly Dictionary<string, (float Last, double At)> progressPulse = new();

    /// <summary>
    /// The run bar. One primary bar carries the whole run; a secondary one, indented and slimmer, carries
    /// the part being worked on now, so the two read as a hierarchy rather than as two things racing.
    ///
    /// A bar with a number stays still: the value is the only thing that moves, plus a brief lift when a
    /// step finishes. Movement belongs to the shape without a number, which is what says "working, nothing
    /// to count yet" while the character is travelling.
    /// </summary>
    public static void ProgressBar(string id, float? fraction, float width, string? label = null, string? caption = null, bool primary = true)
    {
        var now = ImGui.GetTime();
        var h = (primary ? 12f : 6f) * Scale;
        if (!primary)
        {
            var indent = 16f * Scale;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);
            width -= indent;
        }

        if (caption is not null)
        {
            // Caption on the left, count on the right, bar underneath.
            var lineStart = ImGui.GetCursorPos();
            if (primary) Text(caption); else Hint(caption);
            if (!string.IsNullOrEmpty(label))
            {
                var lw = ImGui.CalcTextSize(label, false, 0).X;
                ImGui.SetCursorPos(new Vector2(lineStart.X + width - lw, lineStart.Y));
                Hint(label);
            }
            ImGui.SetCursorPosX(lineStart.X);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3f * Scale);
            label = null;
        }

        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var r = h / 2;

        // Track: a still, shallow groove. Anything moving here would read as a second bar.
        dl.AddRectFilled(pos, pos + new Vector2(width, h), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.07f)), r);
        dl.AddRect(pos, pos + new Vector2(width, h), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.35f)), r);

        if (fraction is { } target)
        {
            target = Math.Clamp(target, 0f, 1f);
            var (shown, at) = progressAnim.TryGetValue(id, out var st) ? st : (target, now);
            var dt = (float)Math.Clamp(now - at, 0, 0.1);
            shown += (target - shown) * (1f - MathF.Exp(-dt * 9f));
            if (Math.Abs(target - shown) < 0.0015f) shown = target;
            progressAnim[id] = (shown, now);

            // A step finishing gives the bar a brief lift, so progress is felt as well as read.
            var (lastSeen, pulseAt) = progressPulse.TryGetValue(id, out var ps) ? ps : (target, 0d);
            if (target > lastSeen + 0.0005f) pulseAt = now;
            progressPulse[id] = (target, pulseAt);
            var pulse = (float)Math.Clamp(1 - (now - pulseAt) / 0.45, 0, 1);

            if (shown > 0f)
            {
                var fillW = Math.Max(h, width * shown);
                var end = pos + new Vector2(fillW, h);
                dl.AddRectFilled(pos, end, ImGui.GetColorU32(Accent * new Vector4(1, 1, 1, primary ? 1f : 0.6f)), r);
                dl.PushClipRect(pos, end, true);
                dl.AddRectFilled(pos, new Vector2(end.X, pos.Y + h * 0.55f), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.12f)), r, ImDrawFlags.RoundCornersTop);
                if (fillW > h * 1.4f)
                    dl.AddRectFilled(new Vector2(end.X - 3f * Scale, pos.Y), end, ImGui.GetColorU32(AccentSoft * new Vector4(1, 1, 1, 0.6f)), r);
                if (pulse > 0.01f)
                    dl.AddRectFilled(pos, end, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.15f * pulse * pulse)), r);
                dl.PopClipRect();
            }
        }
        else
        {
            // Nothing to count: a pill eases back and forth, leaving a short trail behind it.
            var t = (float)((now * 0.85) % 2.0);
            var phase = t < 1f ? t : 2f - t;
            var eased = phase < 0.5f ? 2f * phase * phase : 1f - MathF.Pow(-2f * phase + 2f, 2f) / 2f;
            var pillW = width * 0.26f;
            var x = pos.X + (width - pillW) * eased;
            dl.PushClipRect(pos, pos + new Vector2(width, h), true);
            var back = t < 1f;
            var trail = back ? new Vector2(x - pillW * 0.55f, pos.Y) : new Vector2(x + pillW, pos.Y);
            var trailEnd = back ? new Vector2(x, pos.Y + h) : new Vector2(x + pillW * 1.55f, pos.Y + h);
            dl.AddRectFilled(trail, trailEnd, ImGui.GetColorU32(Accent * new Vector4(1, 1, 1, 0.18f)), r);
            dl.AddRectFilled(new Vector2(x, pos.Y), new Vector2(x + pillW, pos.Y + h), ImGui.GetColorU32(Accent * new Vector4(1, 1, 1, 0.85f)), r);
            dl.AddRectFilled(new Vector2(x, pos.Y), new Vector2(x + pillW, pos.Y + h * 0.55f), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.14f)), r);
            dl.PopClipRect();
        }

        ImGui.Dummy(new Vector2(width, h));
        if (!string.IsNullOrEmpty(label))
        {
            Gap(0.3f);
            Centered(label, muted: true);
        }
    }

    /// <summary>A quiet "n of m · 42%" for the bar; empty while there is nothing to count.</summary>
    public static string ProgressLabel(int done, int total) =>
        total <= 0 ? string.Empty : $"{done} of {total}";

    /// <summary>The top of a running screen: the logo breathing softly, the title, and the step under way.</summary>
    public static void RunningHeader(ImTextureID logo, string title, string? status)
    {
        var size = 72f * Scale;
        var avail = ImGui.GetContentRegionAvail();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0, avail.Y * 0.14f));
        if (!logo.IsNull)
        {
            var breathe = Reduced ? 0.5f : 0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * 1.9f);
            ImGui.SetCursorPosX(Math.Max(0, (ImGui.GetWindowWidth() - size) / 2));
            var pos = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(size, size));
            var dl = ImGui.GetWindowDrawList();
            var mid = pos + new Vector2(size / 2, size / 2);
            // Four rings of falling alpha read as a soft glow; one flat disc reads as a grey plate.
            for (var i = 0; i < 4; i++)
            {
                var spread = size * (0.52f + i * 0.11f) + breathe * 2.5f * Scale;
                dl.AddCircleFilled(mid, spread, ImGui.GetColorU32(Accent * new Vector4(1, 1, 1, 0.05f - i * 0.011f)), 48);
            }
            dl.AddImageRounded(logo, pos, pos + new Vector2(size, size), Vector2.Zero, Vector2.One,
                ImGui.GetColorU32(new Vector4(1, 1, 1, (0.82f + 0.12f * breathe) * ImGui.GetStyle().Alpha)), 16f * Scale);
            Gap(0.6f);
        }
        Centered(title);
        // The step under way rewrites itself several times a second during a run, so it cross-fades.
        if (!string.IsNullOrEmpty(status)) CenteredSwap($"run:{title}", status, muted: true);
    }

    // ---------- cards & banners ----------

    /// <summary>
    /// A rounded, softly raised panel that grows with its content. Draw inside the returned scope; dispose to close.
    /// </summary>
    public static IDisposable Card(string id) => new CardScope(id);

    private sealed class CardScope : IDisposable
    {
        private readonly ImDrawListPtr dl;
        private readonly Vector2 start;
        private readonly float width;
        private readonly float pad;
        private readonly IDisposable id;

        public CardScope(string key)
        {
            id = ImRaii.PushId(key);
            dl = ImGui.GetWindowDrawList();
            pad = 12f * Scale;
            start = ImGui.GetCursorScreenPos();
            width = ImGui.GetContentRegionAvail().X;
            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);
            ImGui.SetCursorScreenPos(start + new Vector2(pad, pad));
            ImGui.BeginGroup();
            ImGui.PushItemWidth(Math.Max(80f, width - pad * 2));
            // Wrapped text stops at the card's inner edge rather than the window's, so a long sentence
            // never runs out under the card's own border.
            ImGui.PushTextWrapPos(start.X - ImGui.GetWindowPos().X + width - pad);
        }

        public void Dispose()
        {
            ImGui.PopTextWrapPos();
            ImGui.PopItemWidth();
            ImGui.EndGroup();
            var bottom = ImGui.GetItemRectMax().Y + pad;
            var max = new Vector2(start.X + width, bottom);
            dl.ChannelsSetCurrent(0);
            // A card lifts a shade under the cursor: enough to say "this is one thing", not enough to distract.
            var key = $"card:{id.GetHashCode()}:{start.Y:F0}";
            var hv = Smooth(key, ImGui.IsMouseHoveringRect(start, max, false) ? 1f : 0f, 14f);
            dl.AddRectFilled(start, max, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.035f + 0.022f * hv)), Rounding);
            dl.AddRect(start, max, ImGui.GetColorU32(Mix(InkLine, InkEdge, hv)), Rounding);
            dl.ChannelsMerge();
            ImGui.SetCursorScreenPos(new Vector2(start.X, bottom));
            ImGui.Dummy(new Vector2(width, Space));
            id.Dispose();
        }
    }

    /// <summary>A tinted one-line notice with a colour bar on its left edge. Returns true when its dismiss link is clicked.</summary>
    private static readonly Dictionary<string, double> dismissing = new();

    public static bool Banner(Vector4 color, string lead, string text, bool dismissible = true, string dismissLabel = "Dismiss", (string Label, Action Click)? link = null)
    {
        // Dismissing plays out: the banner fades and only then reports itself gone.
        var bid = $"banner:{lead}:{text}";
        var a = Appear(bid, 0.28f);
        if (dismissing.TryGetValue(bid, out var goneAt))
        {
            var left = (float)Math.Clamp(1 - (ImGui.GetTime() - goneAt) / 0.22, 0, 1);
            if (left <= 0f) { dismissing.Remove(bid); return true; }
            a = left;
            dismissible = false;
        }
        using var alpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, a);
        var h = ImGui.GetFrameHeight() + 8f * Scale;
        var slot = ImGui.GetCursorScreenPos();
        var pos = slot + new Vector2(0, -6f * Scale * (1f - a));
        var w = ImGui.GetContentRegionAvail().X;
        var dl = ImGui.GetWindowDrawList();
        color *= new Vector4(1, 1, 1, a);
        dl.AddRectFilled(pos, pos + new Vector2(w, h), ImGui.GetColorU32(color * new Vector4(1, 1, 1, 0.09f)), Rounding);
        dl.AddRectFilled(pos, pos + new Vector2(3f * Scale, h), ImGui.GetColorU32(color), Rounding, ImDrawFlags.RoundCornersLeft);
        ImGui.SetCursorScreenPos(pos + new Vector2(12f * Scale, 4f * Scale));
        ImGui.AlignTextToFramePadding();
        TextColored(color, lead);
        ImGui.SameLine();
        var dismissW = dismissible ? ImGui.CalcTextSize(dismissLabel, false, 0).X + 18f * Scale : 0f;
        var linkW = link is null ? 0f : ImGui.CalcTextSize(link.Value.Label, false, 0).X + 18f * Scale;
        using (ImRaii.TextWrapPos(pos.X + w - dismissW - linkW - 8f * Scale))
            Text(text);
        var clicked = false;
        if (link is not null)
        {
            ImGui.SameLine();
            ImGui.SetCursorScreenPos(new Vector2(pos.X + w - dismissW - linkW, pos.Y + 4f * Scale));
            if (LinkButton(link.Value.Label)) link.Value.Click();
        }
        if (dismissible)
        {
            ImGui.SameLine();
            ImGui.SetCursorScreenPos(new Vector2(pos.X + w - dismissW, pos.Y + 4f * Scale));
            clicked = LinkButton(dismissLabel);
        }
        ImGui.SetCursorScreenPos(new Vector2(slot.X, Math.Max(ImGui.GetCursorScreenPos().Y, slot.Y + h)));
        ImGui.Dummy(new Vector2(w, 0));
        // Clicking Dismiss starts the fade; the caller is told it is gone only once the fade has played.
        if (clicked) dismissing[bid] = ImGui.GetTime();
        return false;
    }

    // ---------- inputs ----------

    public static bool InputText(string label, string hint, ref string value, int maxLength = 128) =>
        ImGui.InputTextWithHint(label, hint, ref value, maxLength);

    /// <summary>Search box with a magnifier glyph drawn inside its left edge.</summary>
    public static bool SearchBox(string id, ref string value, float width)
    {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.SetNextItemWidth(width);
        using var pad = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(26f * Scale, ImGui.GetStyle().FramePadding.Y));
        var changed = ImGui.InputTextWithHint(id, "Search", ref value, 64);
        var dl = ImGui.GetWindowDrawList();
        var y = pos.Y + (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) / 2;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            dl.AddText(new Vector2(pos.X + 9f * Scale, y), ImGui.GetColorU32(Muted * new Vector4(1, 1, 1, 0.8f)), FontAwesomeIcon.Search.ToIconString());
        return changed;
    }

    public static bool InputInt(string label, ref int value, int step = 1) =>
        ImGui.InputInt(label, ref value, step, step * 10, "%d", ImGuiInputTextFlags.None);

    public static bool InputLong(string label, ref long value, long step = 1000)
    {
        var v = (int)Math.Clamp(value, int.MinValue, int.MaxValue);
        if (!ImGui.InputInt(label, ref v, (int)step, (int)step * 10, "%d", ImGuiInputTextFlags.None)) return false;
        value = Math.Max(0, v);
        return true;
    }

    public static bool InputUInt(string label, ref uint value, uint step = 10)
    {
        var v = (int)Math.Min(value, int.MaxValue);
        if (!ImGui.InputInt(label, ref v, (int)step, (int)step * 10, "%d", ImGuiInputTextFlags.None)) return false;
        value = (uint)Math.Max(0, v);
        return true;
    }

    public static bool SliderDouble(string label, ref double value, double min, double max, string format = "%.2f")
    {
        var f = (float)value;
        if (!ImGui.SliderFloat(label, ref f, (float)min, (float)max, format, ImGuiSliderFlags.None)) return false;
        value = f;
        return true;
    }

    /// <summary>
    /// A dropdown built from one Selectable per option, so the popup is exactly as tall as its options.
    /// The generic ImGui.Combo helper sized its popup wrongly inside our cards; this is what the review
    /// window's action picker has always used.
    /// </summary>
    public static bool Combo(string label, ref int index, IReadOnlyList<string> items)
    {
        var preview = index >= 0 && index < items.Count ? items[index] : string.Empty;
        using var combo = ImRaii.Combo(label, preview, ImGuiComboFlags.HeightLargest);
        if (!combo) return false;
        var changed = false;
        for (var i = 0; i < items.Count; i++)
        {
            var selected = i == index;
            if (ImGui.Selectable(items[i], selected, ImGuiSelectableFlags.None, Vector2.Zero) && !selected) { index = i; changed = true; }
            if (selected) ImGui.SetItemDefaultFocus();
        }
        return changed;
    }

    public static bool ComboEnum<T>(string label, ref T value, Func<T, string>? display = null) where T : struct, Enum
    {
        var values = Enum.GetValues<T>();
        var names = values.Select(v => display?.Invoke(v) ?? v.ToString()).ToList();
        var idx = Array.IndexOf(values, value);
        if (idx < 0) idx = 0;
        if (!Combo(label, ref idx, names)) return false;
        value = values[idx];
        return true;
    }

    /// <summary>A row of mutually exclusive options drawn as one pill group; the selected one is filled.</summary>
    public static bool Segmented<T>(string id, ref T value, IReadOnlyList<(T Value, string Label)> options) where T : struct
    {
        var changed = false;
        using var _ = ImRaii.PushId(id);
        var pad = ImGui.GetStyle().FramePadding;
        var h = ImGui.GetFrameHeight();
        var widths = options.Select(o => ImGui.CalcTextSize(o.Label, false, 0).X + pad.X * 2 + 6f * Scale).ToList();
        var total = widths.Sum() + 4f * Scale * 2;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(total, h), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.05f)), h / 2);
        dl.AddRect(pos, pos + new Vector2(total, h), ImGui.GetColorU32(InkLine), h / 2);

        // Where the highlight should be, then where it is: it glides rather than jumps.
        var selectedIndex = -1;
        var offsets = new List<float>();
        var run = 4f * Scale;
        for (var i = 0; i < options.Count; i++)
        {
            offsets.Add(run);
            if (EqualityComparer<T>.Default.Equals(options[i].Value, value)) selectedIndex = i;
            run += widths[i];
        }
        var segKey = $"seg:{ImGui.GetID(id)}";
        if (selectedIndex >= 0)
        {
            var hx = Smooth(segKey + ":x", offsets[selectedIndex], 18f);
            var hw = Smooth(segKey + ":w", widths[selectedIndex], 18f);
            var hmin = new Vector2(pos.X + hx, pos.Y + 2f * Scale);
            var hmax = new Vector2(pos.X + hx + hw, pos.Y + h - 2f * Scale);
            dl.AddRectFilled(hmin, hmax, ImGui.GetColorU32(Accent * new Vector4(1, 1, 1, 0.9f)), (h - 4f * Scale) / 2);
        }

        var x = pos.X + 4f * Scale;
        for (var i = 0; i < options.Count; i++)
        {
            var (v, label) = options[i];
            var selected = i == selectedIndex;
            var w = widths[i];
            ImGui.SetCursorScreenPos(new Vector2(x, pos.Y + 2f * Scale));
            if (ImGui.InvisibleButton($"##seg{i}", new Vector2(w, h - 4f * Scale)) && !selected) { value = v; changed = true; }
            var optKey = $"{segKey}:{i}";
            RecordHover(optKey);
            var hv = Hover(optKey);
            var min = new Vector2(x, pos.Y + 2f * Scale);
            var max = new Vector2(x + w, pos.Y + h - 2f * Scale);
            if (!selected && hv > 0.01f)
                dl.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.07f * hv)), (h - 4f * Scale) / 2);
            var onText = Smooth(optKey + ":t", selected ? 1f : 0f, 18f);
            var tw = ImGui.CalcTextSize(label, false, 0).X;
            var tp = new Vector2(x + (w - tw) / 2, pos.Y + (h - ImGui.GetTextLineHeight()) / 2);
            dl.AddText(tp, ImGui.GetColorU32(Mix(ImGui.GetStyle().Colors[(int)ImGuiCol.Text], OnAccent, onText)), label);
            x += w;
        }
        ImGui.SetCursorScreenPos(pos);
        ImGui.Dummy(new Vector2(total, h));
        return changed;
    }

    public static float SegmentedWidth<T>(IReadOnlyList<(T Value, string Label)> options) where T : struct
    {
        var pad = ImGui.GetStyle().FramePadding;
        return options.Sum(o => ImGui.CalcTextSize(o.Label, false, 0).X + pad.X * 2 + 6f * Scale) + 8f * Scale;
    }

    public static void Tooltip(string text)
    {
        if (string.IsNullOrEmpty(text) || !ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) return;
        using var a = ImRaii.PushStyle(ImGuiStyleVar.Alpha, Appear("tip:" + text, 0.14f));
        using var t = ImRaii.Tooltip();
        using var w = ImRaii.TextWrapPos(380f * Scale);
        ImGui.TextUnformatted(text);
    }

    /// <summary>A setting row: label on the left, control on the right, optional one-line hint under the label.</summary>
    public static void SettingLabel(string label, string? hint = null)
    {
        ImGui.AlignTextToFramePadding();
        Text(label);
        if (hint is not null) Tooltip(hint);
    }

    /// <summary>The colour of something waiting for a second click: red, breathing, hard to miss.</summary>
    public static Vector4 Armed()
    {
        if (Reduced) return Danger;
        var pulse = 0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * 5f);
        return Mix(Danger, Cream, 0.28f * pulse);
    }

    public static Vector4 ActionColor(ActionKind action) => action switch
    {
        ActionKind.Discard => Danger,
        ActionKind.VendorSell => Ok,
        ActionKind.ExpertDelivery => Info,
        ActionKind.Desynth => Warn,
        ActionKind.MarketList => Market,
        _ => Muted,
    };

    public static string Gil(long v) => v <= 0 ? "—" : $"{v:N0}g";

    public static void OpenLink(string url) => Dalamud.Utility.Util.OpenLink(url);

    public static string GarlandUrl(uint id) => $"https://www.garlandtools.org/db/#item/{id}";
    public static string UniversalisUrl(uint id) => $"https://universalis.app/market/{id}";
    public static string WikiUrl(string name) => $"https://ffxiv.consolegameswiki.com/wiki/{Uri.EscapeDataString(name.Replace(' ', '_'))}";

    /// <summary>Right-aligns the next item of the given width within the current window.</summary>
    public static void RightAlign(float width) =>
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - width - ImGui.GetStyle().WindowPadding.X));

    /// <summary>
    /// Puts a block of controls at the right edge of the current line, or on a line of its own when the
    /// window is too narrow for both. Without this a footer silently runs off the edge at small sizes.
    /// </summary>
    public static void RightAlignOrWrap(float width, float roomForTextBeside)
    {
        var avail = ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X * 2;
        if (width + roomForTextBeside <= avail) ImGui.SameLine();
        else Gap(0.3f);
        RightAlign(width);
    }
}
