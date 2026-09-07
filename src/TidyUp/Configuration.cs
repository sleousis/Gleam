using Dalamud.Configuration;
using TidyUp.Core.Lists;

namespace TidyUp;

/// <summary>Callback values the game-driving code fires on native dialogs. Exposed so a spike can correct them without a rebuild.</summary>
public sealed class CallbackSettings
{
    /// <summary>SelectYesno: 0 = Yes.</summary>
    public int YesNoConfirm { get; set; } = 0;

    /// <summary>MateriaRetrieveDialog: 0 = Retrieve (verify in spike).</summary>
    public int MateriaRetrieveConfirm { get; set; } = 0;

    /// <summary>SalvageDialog: 0 = Desynthesize (verify in spike).</summary>
    public int SalvageConfirm { get; set; } = 0;

    /// <summary>GrandCompanySupplyList: first callback value selecting a list row for Expert Delivery (verify in spike).</summary>
    public int ExpertDeliverySelect { get; set; } = 1;

    /// <summary>GrandCompanySupplyReward: 0 = Deliver (verify in spike).</summary>
    public int ExpertDeliveryConfirm { get; set; } = 0;

    /// <summary>English label of the inventory context menu entry used to sell at an NPC shop.</summary>
    public string SellLabel { get; set; } = "Sell";

    /// <summary>English label of the inventory context menu entry that opens materia retrieval.</summary>
    public string RetrieveMateriaLabel { get; set; } = "Retrieve Materia";

    /// <summary>How long to wait for the game to confirm one action before treating it as failed.</summary>
    public int ActionTimeoutMs { get; set; } = 4000;

    public int RateLimitMs { get; set; } = 250;
}

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public ProfileStore Profiles { get; set; } = new();
    public ItemList ProtectList { get; set; } = new();
    public ItemList AlwaysDiscardList { get; set; } = new();
    public CallbackSettings Callbacks { get; set; } = new();

    public bool UseUniversalis { get; set; } = true;
    public bool UseAllaganTools { get; set; } = true;
    public bool ShowAltSections { get; set; } = true;
    public bool GamepadNavigation { get; set; } = true;
    public bool ChatSummaryAfterRun { get; set; } = true;

    /// <summary>Set once the first spike run confirmed the discard path on this machine.</summary>
    public bool SpikesVerified { get; set; } = false;

    /// <summary>Glamour plate item ids seen the last time the dresser was open, per character, so the dresser rule has data before plates reload.</summary>
    public Dictionary<ulong, List<uint>> LastKnownPlateItems { get; set; } = new();

    public event Action? Saved;

    public void Save(Dalamud.Plugin.IDalamudPluginInterface pi)
    {
        pi.SavePluginConfig(this);
        Saved?.Invoke();
    }
}
