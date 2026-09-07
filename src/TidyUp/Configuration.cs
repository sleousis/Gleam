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
    public int ActionTimeoutMs { get; set; } = 8000;

    public int RateLimitMs { get; set; } = 250;
}

/// <summary>Everything the hands-free mode needs. Off by default; it moves the character and drives NPC menus.</summary>
public sealed class AutomationSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>Travel to an inn with Lifestream when retainers, sells, or the dresser are involved.</summary>
    public bool TravelToInn { get; set; } = true;

    /// <summary>Lifestream inn index; null lets Lifestream pick.</summary>
    public int? InnIndex { get; set; } = null;

    public bool VisitRetainers { get; set; } = true;
    /// <summary>Sell vendor rows at a merchant NPC found nearby after the other legs.</summary>
    public bool SellAtVendor { get; set; } = true;

    /// <summary>Client-language name of a merchant NPC whose shop window allows selling.</summary>
    public string VendorNpcName { get; set; } = "Merchant & Mender";

    /// <summary>If the vendor shows a menu first, the entry that opens the shop window.</summary>
    public string VendorMenuText { get; set; } = "Purchase";
    public bool VisitDresser { get; set; } = true;
    public bool OpenSaddlebag { get; set; } = true;

    /// <summary>Client-language names of the objects to walk to. English defaults; change for other clients.</summary>
    public string BellObjectName { get; set; } = "Summoning Bell";
    public string DresserObjectName { get; set; } = "Glamour Dresser";

    /// <summary>Substrings matched against the retainer menu entries.</summary>
    public string EntrustMenuText { get; set; } = "Entrust";
    public string QuitMenuText { get; set; } = "Quit";

    /// <summary>English MainCommand name that opens the saddlebag.</summary>
    public string SaddlebagCommandName { get; set; } = "Chocobo Saddlebag";

    /// <summary>RetainerList callback: first value, then the retainer's list index.</summary>
    public int RetainerListSelect { get; set; } = 2;

    public float InteractRange { get; set; } = 3.0f;
    public int TravelTimeoutSeconds { get; set; } = 120;
    public int StepTimeoutSeconds { get; set; } = 20;

    /// <summary>When a container reveals rows that were not in the accepted plan, pause and ask rather than act.</summary>
    public bool PauseForUnseenRows { get; set; } = true;

    // ---- Grand Company leg (Expert Delivery) ----

    public bool VisitGrandCompany { get; set; } = true;

    /// <summary>Client-language name of the NPC that opens supply missions.</summary>
    public string PersonnelOfficerName { get; set; } = "Personnel Officer";

    /// <summary>Substring of the officer's menu entry that opens supply and provisioning missions.</summary>
    public string GcSupplyMenuText { get; set; } = "supply";

    /// <summary>Grand Company id (1 Maelstrom, 2 Twin Adder, 3 Immortal Flames) → Lifestream teleport target for the HQ city.</summary>
    public Dictionary<byte, string> GcCityAetheryte { get; set; } = new()
    {
        [1] = "Limsa Lominsa Lower Decks",
        [2] = "New Gridania",
        [3] = "Ul'dah - Steps of Nald",
    };

    /// <summary>Grand Company id → aethernet shard next to the HQ, or empty to walk from the aetheryte.</summary>
    public Dictionary<byte, string> GcAethernetShard { get; set; } = new()
    {
        [1] = "The Aftcastle",
        [2] = string.Empty,
        [3] = string.Empty,
    };

    /// <summary>Callback values that switch the supply window to the Expert Delivery tab.</summary>
    public string ExpertDeliveryTabCallback { get; set; } = "0,2";
}

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public ProfileStore Profiles { get; set; } = new();
    public ItemList ProtectList { get; set; } = new();
    public ItemList AlwaysDiscardList { get; set; } = new();
    public CallbackSettings Callbacks { get; set; } = new();
    public AutomationSettings Automation { get; set; } = new();

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
