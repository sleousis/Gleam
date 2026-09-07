using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using TidyUp.Core.Model;

namespace TidyUp.Windows;

/// <summary>
/// Thin wrappers so ImGui binding signatures live in one place, plus the few visual primitives the
/// windows share: one accent, one muted tone, semantic colours only for actions and states.
/// </summary>
internal static class Ui
{
    public static readonly Vector4 Accent = new(0.85f, 0.71f, 0.29f, 1f);
    public static readonly Vector4 Danger = ImGuiColors.DalamudRed;
    public static readonly Vector4 Ok = ImGuiColors.HealerGreen;
    public static readonly Vector4 Warn = ImGuiColors.DalamudOrange;
    public static readonly Vector4 Info = ImGuiColors.TankBlue;
    public static readonly Vector4 Muted = ImGuiColors.DalamudGrey;

    public static float Scale => ImGuiHelpers.GlobalScale;
    public static float Space => 8f * Scale;

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

    /// <summary>A quiet section label with breathing room above it. No rules, no caps.</summary>
    public static void Section(string title)
    {
        ImGui.Dummy(new Vector2(0, Space));
        TextColored(Muted, title);
        ImGui.Spacing();
    }

    public static void Gap(float multiple = 1f) => ImGui.Dummy(new Vector2(0, Space * multiple));

    public static bool Button(string label, float width = 0f) => ImGui.Button(label, new Vector2(width, 0f));

    /// <summary>The one filled button on a screen. Everything else is a plain button or a link.</summary>
    public static bool PrimaryButton(string label, float width = 0f, bool danger = false)
    {
        var color = danger ? Danger : Accent;
        using var c = ImRaii.PushColor(ImGuiCol.Button, color * new Vector4(1, 1, 1, 0.75f));
        using var h = ImRaii.PushColor(ImGuiCol.ButtonHovered, color * new Vector4(1, 1, 1, 0.9f));
        using var a = ImRaii.PushColor(ImGuiCol.ButtonActive, color);
        using var t = ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.08f, 0.08f, 0.08f, 1f));
        return ImGui.Button(label, new Vector2(width, 0f));
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
    public static void Pill(string text, Vector4 color)
    {
        using var b = ImRaii.PushColor(ImGuiCol.Button, color * new Vector4(1, 1, 1, 0.18f));
        using var h = ImRaii.PushColor(ImGuiCol.ButtonHovered, color * new Vector4(1, 1, 1, 0.18f));
        using var a = ImRaii.PushColor(ImGuiCol.ButtonActive, color * new Vector4(1, 1, 1, 0.18f));
        using var t = ImRaii.PushColor(ImGuiCol.Text, color);
        using var r = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 10f * Scale);
        using var p = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(8f * Scale, 1f * Scale));
        ImGui.Button(text, new Vector2(0, 0));
    }

    public static bool InputText(string label, string hint, ref string value, int maxLength = 128) =>
        ImGui.InputTextWithHint(label, hint, ref value, maxLength);

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

    /// <summary>A row of mutually exclusive options drawn as adjoining buttons; the selected one is filled.</summary>
    public static bool Segmented<T>(string id, ref T value, IReadOnlyList<(T Value, string Label)> options) where T : struct
    {
        var changed = false;
        using var _ = ImRaii.PushId(id);
        using var sp = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(2f * Scale, 0));
        for (var i = 0; i < options.Count; i++)
        {
            var (v, label) = options[i];
            var selected = EqualityComparer<T>.Default.Equals(v, value);
            var buttonColor = selected ? ImGui.GetColorU32(Accent * new Vector4(1, 1, 1, 0.75f)) : ImGui.GetColorU32(ImGuiCol.FrameBg);
            var textColor = selected ? ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.08f, 1f)) : ImGui.GetColorU32(ImGuiCol.Text);
            using var b = ImRaii.PushColor(ImGuiCol.Button, buttonColor, true);
            using var t = ImRaii.PushColor(ImGuiCol.Text, textColor, true);
            if (ImGui.Button(label, new Vector2(0, 0)) && !selected) { value = v; changed = true; }
            if (i < options.Count - 1) ImGui.SameLine();
        }
        return changed;
    }

    public static void Tooltip(string text)
    {
        if (string.IsNullOrEmpty(text) || !ImGui.IsItemHovered()) return;
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
