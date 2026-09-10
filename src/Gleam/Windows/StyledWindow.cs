using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Gleam.Windows;

/// <summary>A Dalamud window that wears the Gleam style: pushed before Begin, popped after End.</summary>
public abstract class StyledWindow : Window
{
    private IDisposable? style;
    private IDisposable? fade;
    private double openedAt;

    protected StyledWindow(string name, ImGuiWindowFlags flags = ImGuiWindowFlags.None) : base(name, flags)
    {
    }

    /// <summary>A title-bar button that opens another Gleam window, so every window can reach every other.</summary>
    public void AddNav(FontAwesomeIcon icon, string tooltip, Action open)
    {
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = icon,
            Click = _ => open(),
            ShowTooltip = () => ImGui.SetTooltip(tooltip),
        });
    }

    public override void OnOpen()
    {
        openedAt = ImGui.GetTime();
        base.OnOpen();
    }

    public override void PreDraw()
    {
        style = Ui.PushWindowStyle();
        // Windows fade up over a quarter second instead of popping.
        var t = (float)Math.Clamp((ImGui.GetTime() - openedAt) / 0.24, 0, 1);
        if (t < 1f && !Ui.Reduced) fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.15f + 0.85f * Ui.EaseOut(t));
        base.PreDraw();
    }

    public override void PostDraw()
    {
        base.PostDraw();
        fade?.Dispose();
        fade = null;
        style?.Dispose();
        style = null;
    }
}
