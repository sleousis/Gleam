using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using TidyUp.Core.Model;

namespace TidyUp.Windows;

/// <summary>Thin wrappers so ImGui binding signatures live in one place.</summary>
internal static class Ui
{
    public static readonly Vector4 Gold = new(0.85f, 0.71f, 0.29f, 1f);
    public static readonly Vector4 Danger = ImGuiColors.DalamudRed;
    public static readonly Vector4 Ok = ImGuiColors.HealerGreen;
    public static readonly Vector4 Warn = ImGuiColors.DalamudOrange;
    public static readonly Vector4 Info = ImGuiColors.TankBlue;
    public static readonly Vector4 Muted = ImGuiColors.DalamudGrey;

    public static float Scale => ImGuiHelpers.GlobalScale;

    public static void Text(string text) => ImGui.TextUnformatted(text);

    public static void TextColored(Vector4 color, string text)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
    }

    public static void Muted2(string text) => ImGui.TextDisabled(text);

    public static bool Button(string label, float width = 0f) => ImGui.Button(label, new Vector2(width, 0f));

    public static bool ButtonColored(string label, Vector4 color, float width = 0f)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Button, color * new Vector4(1, 1, 1, 0.6f));
        using var h = ImRaii.PushColor(ImGuiCol.ButtonHovered, color * new Vector4(1, 1, 1, 0.8f));
        using var a = ImRaii.PushColor(ImGuiCol.ButtonActive, color);
        return ImGui.Button(label, new Vector2(width, 0f));
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

    public static void Tooltip(string text)
    {
        if (!ImGui.IsItemHovered()) return;
        using var t = ImRaii.Tooltip();
        using var w = ImRaii.TextWrapPos(400f * Scale);
        ImGui.TextUnformatted(text);
    }

    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        Tooltip(text);
    }

    public static void Header(string text)
    {
        ImGui.Spacing();
        TextColored(Gold, text.ToUpperInvariant());
        ImGui.Separator();
    }

    public static Vector4 ActionColor(ActionKind action) => action switch
    {
        ActionKind.Discard => Danger,
        ActionKind.VendorSell => Ok,
        ActionKind.ExpertDelivery => Info,
        ActionKind.Desynth => Warn,
        _ => Muted,
    };

    public static Vector4 ConfidenceColor(Confidence c) => c switch
    {
        Confidence.User => Gold,
        Confidence.High => Ok,
        Confidence.Medium => Warn,
        _ => Muted,
    };

    public static string Gil(long v) => v <= 0 ? "—" : $"{v:N0}g";

    public static void OpenLink(string url) => Dalamud.Utility.Util.OpenLink(url);

    public static string GarlandUrl(uint id) => $"https://www.garlandtools.org/db/#item/{id}";
    public static string UniversalisUrl(uint id) => $"https://universalis.app/market/{id}";
    public static string WikiUrl(string name) => $"https://ffxiv.consolegameswiki.com/wiki/{Uri.EscapeDataString(name.Replace(' ', '_'))}";
}
