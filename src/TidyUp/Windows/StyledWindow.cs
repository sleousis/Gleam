using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace TidyUp.Windows;

/// <summary>A Dalamud window that wears the Tidy Up style: pushed before Begin, popped after End.</summary>
public abstract class StyledWindow : Window
{
    private IDisposable? style;

    protected StyledWindow(string name, ImGuiWindowFlags flags = ImGuiWindowFlags.None) : base(name, flags)
    {
    }

    public override void PreDraw()
    {
        style = Ui.PushWindowStyle();
        base.PreDraw();
    }

    public override void PostDraw()
    {
        base.PostDraw();
        style?.Dispose();
        style = null;
    }
}
