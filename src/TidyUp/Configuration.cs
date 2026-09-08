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

    /// <summary>Retainer inventory context entry that moves an item back to the player's bags.</summary>
    public string RetrieveFromRetainerLabel { get; set; } = "Retrieve from Retainer";

    /// <summary>Context entry that opens the RetainerSell window while a retainer's sell list is up.</summary>
    public string PutUpForSaleLabel { get; set; } = "Put Up for Sale";

    /// <summary>Item menu entry that sorts the container the item sits in.</summary>
    public string SortLabel { get; set; } = "Sort";

    /// <summary>Retainer inventory item menu entry that sells the item to the retainer for the vendor price.</summary>
    public string RetainerSellItemLabel { get; set; } = "Have Retainer Sell Items";

    /// <summary>Bag item menu entry (retainer open) that hands the stack to the retainer.</summary>
    public string EntrustLabel { get; set; } = "Entrust to Retainer";

    /// <summary>RetainerSell callback value that confirms the listing (verify in spike).</summary>
    public int RetainerSellConfirm { get; set; } = 0;

    /// <summary>How long to wait for the game to confirm one action before treating it as failed.</summary>
    public int ActionTimeoutMs { get; set; } = 8000;

    public int RateLimitMs { get; set; } = 250;
}

/// <summary>What the pilot does with rows it only discovers once a container is open (never-cached retainers, the dresser).</summary>
public enum UnseenRowsMode
{
    /// <summary>Leave them for the next review.</summary>
    Skip,
    /// <summary>Open the review and wait for the user.</summary>
    Ask,
    /// <summary>Apply the same rules and clean what they would have checked by default.</summary>
    Clean,
}

/// <summary>Everything the hands-free mode needs. Off by default; it moves the character and drives NPC menus.</summary>
public sealed class AutomationSettings
{
    /// <summary>Rows discovered only once a container opens. Clean means the run is truly hands-free.</summary>
    public UnseenRowsMode UnseenRows { get; set; } = UnseenRowsMode.Clean;

    /// <summary>
    /// Also travel to containers with nothing selected, to scan and clean them. Off: a run only goes
    /// where the ticked rows are, so ticking one bag item never triggers a trip to the inn.
    /// </summary>
    public bool VisitContainersWithoutRows { get; set; } = false;

    public bool Enabled { get; set; } = false;

    /// <summary>Travel to an inn with Lifestream when retainers, sells, or the dresser are involved.</summary>
    public bool TravelToInn { get; set; } = true;

    /// <summary>Lifestream inn index; null lets Lifestream pick.</summary>
    public int? InnIndex { get; set; } = null;

    public bool VisitRetainers { get; set; } = true;
    /// <summary>Sell vendor rows at a merchant NPC found nearby after the other legs.</summary>
    public bool SellAtVendor { get; set; } = true;

    /// <summary>Client-language name of a merchant NPC whose shop window allows selling.</summary>
    public string VendorNpcName { get; set; } = string.Empty;

    /// <summary>If the vendor shows a menu first, the entry that opens the shop window.</summary>
    public string VendorMenuText { get; set; } = "Purchase";

    /// <summary>Lifestream destination with a merchant right by the aetheryte, used when none is within reach.</summary>
    public string VendorAetheryte { get; set; } = "Limsa Lominsa Lower Decks";
    public bool VisitDresser { get; set; } = true;
    public bool OpenSaddlebag { get; set; } = true;

    /// <summary>Client-language names of the objects to walk to. English defaults; change for other clients.</summary>
    public string BellObjectName { get; set; } = "Summoning Bell";
    public string DresserObjectName { get; set; } = "Glamour Dresser";

    /// <summary>Substrings matched against the retainer menu entries.</summary>
    public string EntrustMenuText { get; set; } = "Entrust";
    public string QuitMenuText { get; set; } = "Quit";

    /// <summary>Retainer menu entry that opens the sell list for the player's own bags.</summary>
    public string SellFromBagsMenuText { get; set; } = "your inventory";

    /// <summary>Retainer menu entry that opens the sell list for the retainer's inventory.</summary>
    public string SellFromRetainerMenuText { get; set; } = "retainer's inventory";

    /// <summary>English MainCommand name that opens the saddlebag.</summary>
    public string SaddlebagCommandName { get; set; } = "Chocobo Saddlebag";

    /// <summary>RetainerList callback: first value, then the retainer's list index.</summary>
    public int RetainerListSelect { get; set; } = 2;

    public float InteractRange { get; set; } = 3.0f;
    public int TravelTimeoutSeconds { get; set; } = 120;
    public int StepTimeoutSeconds { get; set; } = 20;


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

/// <summary>Named organizer layouts and which one is in use.</summary>
public sealed class OrganizerSettings
{
    public List<Core.Organizer.Model.OrganizerPlan> Plans { get; set; } = new();
    public Guid? ActivePlanId { get; set; }

    public Core.Organizer.Model.OrganizerPlan? Active =>
        Plans.FirstOrDefault(p => p.Id == ActivePlanId) ?? Plans.FirstOrDefault();
}

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;

    public ProfileStore Profiles { get; set; } = new();
    public ItemList ProtectList { get; set; } = new();
    public ItemList AlwaysDiscardList { get; set; } = new();
    public CallbackSettings Callbacks { get; set; } = new();
    public AutomationSettings Automation { get; set; } = new();
    public OrganizerSettings Organizer { get; set; } = new();

    /// <summary>When materia cannot be retrieved from an item: leave the item, or act and lose the materia.</summary>
    public bool ActWhenMateriaFails { get; set; } = false;

    /// <summary>Run the game's own sort on each container that was cleaned, once the run is over.</summary>
    public bool SortAfterRun { get; set; } = true;

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

    /// <summary>Brings a config written by an older build up to current defaults where the old default was a mistake.</summary>
    public bool Migrate()
    {
        var changed = false;
        if (Version < 2)
        {
            // v1 hid retainer rows behind a collapsed header and paused the pilot at every container.
            Profiles.Account.RetainerSectionsCollapsed = false;
            foreach (var o in Profiles.Overrides) o.Values.RetainerSectionsCollapsed = false;
            Version = 2;
            changed = true;
        }
        if (Version < 3)
        {
            if (Callbacks.ActionTimeoutMs < 8000) Callbacks.ActionTimeoutMs = 8000;
            Version = 3;
            changed = true;
        }
        if (Version < 4)
        {
            // The Cautious preset is gone; anyone on it lands on Balanced.
            if (Profiles.Account.Thresholds.Policy == Core.Rules.ActionPolicy.SellOnly) Profiles.Account.ApplyPreset(Core.Rules.PresetName.Vendor);
            foreach (var o in Profiles.Overrides)
                if (o.Values.Thresholds.Policy == Core.Rules.ActionPolicy.SellOnly) o.Values.ApplyPreset(Core.Rules.PresetName.Vendor);
            Version = 4;
            changed = true;
        }
        if (Version < 5)
        {
            // These stopped being settings; make sure nobody is stuck with an old "off".
            UseUniversalis = true;
            UseAllaganTools = true;
            ActWhenMateriaFails = false;
            Automation.OpenSaddlebag = Automation.VisitRetainers = Automation.VisitDresser = Automation.SellAtVendor = Automation.VisitGrandCompany = true;
            Automation.VisitContainersWithoutRows = false;
            Profiles.Account.EnabledRules.Add(Core.Rules.MarketPricePostProcessor.RuleId);
            Version = 5;
            changed = true;
        }
        if (Version < 6)
        {
            Profiles.Account.EnabledRules.Add(Core.Rules.RegisteredDuplicateRule.RuleId);
            foreach (var o in Profiles.Overrides) o.Values.EnabledRules.Add(Core.Rules.RegisteredDuplicateRule.RuleId);
            Version = 6;
            changed = true;
        }
        if (Version < 7)
        {
            if (Organizer.Plans.Count == 0)
            {
                var starter = Core.Organizer.Model.OrganizerPlan.Starter();
                Organizer.Plans.Add(starter);
                Organizer.ActivePlanId = starter.Id;
            }
            Version = 7;
            changed = true;
        }
        return changed;
    }

    public void Save(Dalamud.Plugin.IDalamudPluginInterface pi)
    {
        pi.SavePluginConfig(this);
        Saved?.Invoke();
    }
}
