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
    public static readonly Vector4 Accent = new(0.87f, 0.72f, 0.31f, 1f);
    public static readonly Vector4 AccentSoft = new(0.96f, 0.86f, 0.55f, 1f);
    public static readonly Vector4 Ink = new(0.075f, 0.09f, 0.12f, 0.985f);
    public static readonly Vector4 InkRaised = new(0.11f, 0.13f, 0.17f, 1f);
    public static readonly Vector4 InkPopup = new(0.10f, 0.12f, 0.155f, 1f);
    public static readonly Vector4 InkLine = new(1f, 1f, 1f, 0.08f);
    public static readonly Vector4 InkEdge = new(1f, 1f, 1f, 0.16f);
    public static readonly Vector4 Cream = new(0.97f, 0.93f, 0.85f, 1f);
    public static readonly Vector4 Danger = new(0.93f, 0.42f, 0.40f, 1f);
    public static readonly Vector4 Ok = new(0.49f, 0.80f, 0.55f, 1f);
    public static readonly Vector4 Warn = new(0.95f, 0.68f, 0.33f, 1f);
    public static readonly Vector4 Info = new(0.47f, 0.68f, 0.93f, 1f);
    public static readonly Vector4 Muted = new(0.62f, 0.66f, 0.72f, 1f);
    public static readonly Vector4 Market = new(0.45f, 0.80f, 0.78f, 1f);
    public static readonly Vector4 OnAccent = new(0.10f, 0.09f, 0.06f, 1f);

    public static float Scale => ImGuiHelpers.GlobalScale;
    public static float Space => 8f * Scale;
    public static float Rounding => 6f * Scale;

    // ---------- window-wide style ----------

    /// <summary>Pushed by every Tidy Up window around Begin/End. Rounded corners, calmer frames, ink ground.</summary>
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
                .Push(ImGuiStyleVar.WindowTitleAlign, new Vector2(0.02f, 0.5f));

            color = ImRaii.PushColor(ImGuiCol.WindowBg, Ink)
                .Push(ImGuiCol.ChildBg, Vector4.Zero)
                .Push(ImGuiCol.PopupBg, InkPopup)
                .Push(ImGuiCol.Border, InkEdge)
                .Push(ImGuiCol.TitleBg, new Vector4(0.06f, 0.075f, 0.10f, 1f))
                .Push(ImGuiCol.TitleBgActive, new Vector4(0.08f, 0.10f, 0.135f, 1f))
                .Push(ImGuiCol.TitleBgCollapsed, new Vector4(0.06f, 0.075f, 0.10f, 0.8f))
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
    public static IDisposable RichTooltip(float width = 340f)
    {
        var style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14f * Scale, 12f * Scale))
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(8f * Scale, 5f * Scale))
            .Push(ImGuiStyleVar.WindowRounding, 8f * Scale);
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
    public static void Header(ImTextureID logo, string title, string subtitle, float rightWidth = 0f, Action? right = null, string? rightNote = null)
    {
        var size = 36f * Scale;
        var start = ImGui.GetCursorPos();
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
            TextColored(AccentSoft, title);
            Hint(subtitle);
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
        ImGui.Dummy(Vector2.Zero);
    }

    // ---------- buttons ----------

    public static bool Button(string label, float width = 0f) => ImGui.Button(label, new Vector2(width, 0f));

    /// <summary>A button with a leading icon.</summary>
    public static bool IconButton(FontAwesomeIcon icon, string label, float width = 0f)
    {
        var id = $"{label}##{icon}";
        var pad = ImGui.GetStyle().FramePadding;
        var textW = ImGui.CalcTextSize(label, false, 0).X;
        var iconW = IconWidth(icon);
        var w = width > 0 ? width : textW + iconW + pad.X * 2 + 6f * Scale;
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.Button($"##{id}", new Vector2(w, 0));
        var dl = ImGui.GetWindowDrawList();
        var h = ImGui.GetFrameHeight();
        var x = pos.X + (w - (textW + iconW + 6f * Scale)) / 2;
        var y = pos.Y + (h - ImGui.GetTextLineHeight()) / 2;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            dl.AddText(new Vector2(x, y), ImGui.GetColorU32(Muted), icon.ToIconString());
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
        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();
        var disabled = ImGui.GetStyle().Alpha < 0.99f;
        var dl = ImGui.GetWindowDrawList();
        var alpha = disabled ? 0.45f : held ? 1f : hovered ? 0.95f : 0.85f;
        var fill = color * new Vector4(1, 1, 1, alpha);
        var r = Rounding;
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
        using var b = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero);
        using var h = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(1, 1, 1, 0.08f));
        using var a = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(1, 1, 1, 0.12f));
        using var t = ImRaii.PushColor(ImGuiCol.Text, Muted);
        return ImGui.Button(label, new Vector2(0, 0));
    }

    /// <summary>Small coloured status chip, e.g. "ready" or "needs saddlebag".</summary>
    public static void Pill(string text, Vector4 color, FontAwesomeIcon? icon = null)
    {
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

    /// <summary>A toggle chip for filters. Active chips fill with the accent.</summary>
    public static bool Chip(string label, bool active)
    {
        using var r = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 10f * Scale);
        using var p = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(9f * Scale, 2f * Scale));
        using var bg = ImRaii.PushColor(ImGuiCol.Button, active ? Accent * new Vector4(1, 1, 1, 0.85f) : new Vector4(1, 1, 1, 0.06f));
        using var bh = ImRaii.PushColor(ImGuiCol.ButtonHovered, active ? Accent : new Vector4(1, 1, 1, 0.11f));
        using var ba = ImRaii.PushColor(ImGuiCol.ButtonActive, active ? Accent : new Vector4(1, 1, 1, 0.15f));
        using var fg = ImRaii.PushColor(ImGuiCol.Text, active ? OnAccent : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
        return ImGui.Button(label, new Vector2(0, 0));
    }

    // ---------- images ----------

    public static void ImageRounded(ImTextureID tex, Vector2 size, float rounding)
    {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        if (tex.IsNull) return;
        ImGui.GetWindowDrawList().AddImageRounded(tex, pos, pos + size, Vector2.Zero, Vector2.One, 0xFFFFFFFF, rounding);
    }

    /// <summary>Faded logo and a line of text, centred. For "nothing here" states.</summary>
    public static void EmptyState(ImTextureID logo, string text, string? hint = null)
    {
        var size = 72f * Scale;
        var avail = ImGui.GetContentRegionAvail();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0, avail.Y * 0.18f));
        if (!logo.IsNull)
        {
            ImGui.SetCursorPosX(Math.Max(0, (ImGui.GetWindowWidth() - size) / 2));
            var pos = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(size, size));
            ImGui.GetWindowDrawList().AddImageRounded(logo, pos, pos + new Vector2(size, size), Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.55f)), 14f * Scale);
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

    /// <summary>Slim accent progress bar with the label drawn to its right.</summary>
    public static void Progress(float fraction, float width, string label)
    {
        var pos = ImGui.GetCursorScreenPos();
        var h = 8f * Scale;
        var dl = ImGui.GetWindowDrawList();
        var f = Math.Clamp(fraction, 0f, 1f);
        dl.AddRectFilled(pos, pos + new Vector2(width, h), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.08f)), h / 2);
        if (f > 0)
            dl.AddRectFilled(pos, pos + new Vector2(Math.Max(h, width * f), h), ImGui.GetColorU32(Accent), h / 2);
        ImGui.Dummy(new Vector2(width, h));
        Centered(label, muted: true);
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
        }

        public void Dispose()
        {
            ImGui.PopItemWidth();
            ImGui.EndGroup();
            var bottom = ImGui.GetItemRectMax().Y + pad;
            var max = new Vector2(start.X + width, bottom);
            dl.ChannelsSetCurrent(0);
            dl.AddRectFilled(start, max, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.035f)), Rounding);
            dl.AddRect(start, max, ImGui.GetColorU32(InkLine), Rounding);
            dl.ChannelsMerge();
            ImGui.SetCursorScreenPos(new Vector2(start.X, bottom));
            ImGui.Dummy(new Vector2(width, Space));
            id.Dispose();
        }
    }

    /// <summary>A tinted one-line notice with a colour bar on its left edge. Returns true when its dismiss link is clicked.</summary>
    public static bool Banner(Vector4 color, string lead, string text, bool dismissible = true)
    {
        var h = ImGui.GetFrameHeight() + 8f * Scale;
        var pos = ImGui.GetCursorScreenPos();
        var w = ImGui.GetContentRegionAvail().X;
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(w, h), ImGui.GetColorU32(color * new Vector4(1, 1, 1, 0.09f)), Rounding);
        dl.AddRectFilled(pos, pos + new Vector2(3f * Scale, h), ImGui.GetColorU32(color), Rounding, ImDrawFlags.RoundCornersLeft);
        ImGui.SetCursorScreenPos(pos + new Vector2(12f * Scale, 4f * Scale));
        ImGui.AlignTextToFramePadding();
        TextColored(color, lead);
        ImGui.SameLine();
        var dismissW = dismissible ? 70f * Scale : 0f;
        using (ImRaii.TextWrapPos(pos.X + w - dismissW - 8f * Scale))
            Text(text);
        var clicked = false;
        if (dismissible)
        {
            ImGui.SameLine();
            ImGui.SetCursorScreenPos(new Vector2(pos.X + w - dismissW, pos.Y + 4f * Scale));
            clicked = LinkButton("Dismiss");
        }
        ImGui.SetCursorScreenPos(new Vector2(pos.X, Math.Max(ImGui.GetCursorScreenPos().Y, pos.Y + h)));
        ImGui.Dummy(new Vector2(w, 0));
        return clicked;
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

    public static bool Combo(string label, ref int index, IReadOnlyList<string> items) =>
        ImGui.Combo(label, ref index, items, items.Count);

    public static bool ComboEnum<T>(string label, ref T value, Func<T, string>? display = null) where T : struct, Enum
    {
        var values = Enum.GetValues<T>();
        var names = values.Select(v => display?.Invoke(v) ?? v.ToString()).ToList();
        var idx = Array.IndexOf(values, value);
        if (idx < 0) idx = 0;
        if (!ImGui.Combo(label, ref idx, names, names.Count)) return false;
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

        var x = pos.X + 4f * Scale;
        for (var i = 0; i < options.Count; i++)
        {
            var (v, label) = options[i];
            var selected = EqualityComparer<T>.Default.Equals(v, value);
            var w = widths[i];
            ImGui.SetCursorScreenPos(new Vector2(x, pos.Y + 2f * Scale));
            if (ImGui.InvisibleButton($"##seg{i}", new Vector2(w, h - 4f * Scale)) && !selected) { value = v; changed = true; }
            var hovered = ImGui.IsItemHovered();
            var min = new Vector2(x, pos.Y + 2f * Scale);
            var max = new Vector2(x + w, pos.Y + h - 2f * Scale);
            if (selected)
                dl.AddRectFilled(min, max, ImGui.GetColorU32(Accent * new Vector4(1, 1, 1, 0.9f)), (h - 4f * Scale) / 2);
            else if (hovered)
                dl.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.07f)), (h - 4f * Scale) / 2);
            var tw = ImGui.CalcTextSize(label, false, 0).X;
            var tp = new Vector2(x + (w - tw) / 2, pos.Y + (h - ImGui.GetTextLineHeight()) / 2);
            dl.AddText(tp, ImGui.GetColorU32(selected ? OnAccent : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]), label);
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
}
