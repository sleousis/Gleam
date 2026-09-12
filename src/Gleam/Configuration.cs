using Dalamud.Configuration;
using Gleam.Core.Lists;
using Gleam.Core.Settings;

namespace Gleam;

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

/// <summary>Everything the hands-free mode needs. Off by default; it moves the character and drives NPC menus.</summary>
public sealed class AutomationSettings : IMigratableAutomation
{
    /// <summary>
    /// Rows discovered only once a container opens. New players start on Skip: rows nobody reviewed wait for the next
    /// look. Clean makes a run fully hands-free. Existing settings keep whatever the player had.
    /// </summary>
    public UnseenRowsMode UnseenRows { get; set; } = UnseenRowsMode.Skip;

    /// <summary>
    /// Also travel to containers with nothing selected, to scan and clean them. Off: a run only goes
    /// where the ticked rows are, so ticking one bag item never triggers a trip to the inn.
    /// </summary>
    public bool VisitContainersWithoutRows { get; set; } = false;

    /// <summary>
    /// Gleam always does the walking and travelling. This is kept only so older configs load; it is forced
    /// on and never shown, and the two travel plugins are stated as requirements instead.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>After AutoRetainer collects a retainer's ventures, discard the junk that landed in the bags.</summary>
    public bool CleanAfterVentures { get; set; } = false;

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
public sealed class OrganizerSettings : IMigratableLayouts
{
    public List<Core.Organizer.Model.OrganizerPlan> Plans { get; set; } = new();
    public Guid? ActivePlanId { get; set; }

    /// <summary>The layout that was in use before the simple screen borrowed the active slot, so it comes back.</summary>
    public Guid? AdvancedPlanId { get; set; }

    /// <summary>
    /// Never serialized: the config serializer would write this plan out a second time and, on load, fill the
    /// very same object in place, appending every rule again on each start.
    /// </summary>
    [Newtonsoft.Json.JsonIgnore]
    public Core.Organizer.Model.OrganizerPlan? Active =>
        Plans.FirstOrDefault(p => p.Id == ActivePlanId) ?? Plans.FirstOrDefault();
}

public sealed class Configuration : IPluginConfiguration, IMigratableSettings
{
    /// <summary>New configs start here and skip the chain. The steps and the number live in <see cref="ConfigMigrations"/>.</summary>
    public const int CurrentVersion = ConfigMigrations.CurrentVersion;

    public int Version { get; set; } = CurrentVersion;

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

    /// <summary>One-time guidance: the chat greeting and the two intro cards, each shown until dismissed.</summary>
    public bool Greeted { get; set; }
    public bool SeenCleanIntro { get; set; }
    public bool SeenOrganizeIntro { get; set; }

    /// <summary>The one first-run screen. Shown once.</summary>
    public bool SeenFirstRun { get; set; }

    /// <summary>A clean has finished at least once, so the offer to organize is worth making.</summary>
    public bool HasCleanedOnce { get; set; }

    /// <summary>The offer to start using the other half has been answered, either way.</summary>
    public bool AnsweredOrganizeOffer { get; set; }

    /// <summary>
    /// What the player asked Gleam for on the first screen. Plenty of people want tidy bags and never want
    /// anything thrown away, so cleaning is a choice, not a toll gate.
    /// </summary>
    public bool UseClean { get; set; } = true;
    public bool UseOrganize { get; set; }

    /// <summary>An organize run has finished at least once, so the offer to clean is worth making.</summary>
    public bool HasOrganizedOnce { get; set; }

    /// <summary>The page the window opens on when nothing else decides.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool StartOnOrganize => UseOrganize && !UseClean;

    /// <summary>
    /// Off: the simple layer only. One list with what will happen, quick setup for where things go, four settings.
    /// On: filters, per-row actions, layouts and rules, and every other setting.
    /// </summary>
    public bool AdvancedMode { get; set; }

    /// <summary>
    /// Off: things ease, fade and count. On: they arrive at their end state at once. For players who find
    /// movement uncomfortable, and for anyone recording where a moving interface is a distraction.
    /// </summary>
    public bool ReduceMotion { get; set; }

    /// <summary>Largest stack per market listing; 0 lists whole stacks.</summary>
    public int MarketListStackSize { get; set; }

    public bool UseUniversalis { get; set; } = true;
    public bool UseAllaganTools { get; set; } = true;
    public bool ShowAltSections { get; set; } = true;
    public bool ChatSummaryAfterRun { get; set; } = true;

    /// <summary>
    /// Retainer names Gleam has seen, by retainer id. The game only lists the retainers of the character you
    /// are on, so without this a layout that sends things to another character's retainer could only show a
    /// number. Names are cheap to keep and never go stale in a way that matters.
    /// </summary>
    public Dictionary<ulong, string> KnownRetainerNames { get; set; } = new();

    /// <summary>
    /// Which character each retainer belongs to, by retainer id, learned whenever the game lists a character's
    /// retainers. It does that only after a summoning bell has been used since logging in, so until then this is
    /// how Gleam knows which of the names above are yours.
    /// </summary>
    public Dictionary<ulong, ulong> RetainerOwners { get; set; } = new();

    /// <summary>
    /// A game version the player chose to go hands-free on before this build of Gleam was checked against it.
    /// Covers that one patch only: the next one is held back again.
    /// </summary>
    public string? AcceptedGameVersion { get; set; }

    /// <summary>The newest release whose "what's new" the player has put away. Null on a fresh install.</summary>
    public string? LastSeenVersion { get; set; }

    /// <summary>The stats page's period (0 a week, 1 a month, 2 a year, 3 all time) and whether it counts every character.</summary>
    public int StatsRange { get; set; } = 1;
    public bool StatsAllCharacters { get; set; }

    /// <summary>One chat line when a milestone is reached.</summary>
    public bool ShowMilestones { get; set; } = true;

    /// <summary>Milestones already earned when the stats page first counted the history are written down quietly, once.</summary>
    public bool StatsMilestonesPrimed { get; set; }

    /// <summary>Set by "Reset statistics": nothing before it counts. The histories themselves are left alone.</summary>
    public DateTimeOffset? StatsResetAt { get; set; }

    /// <summary>Glamour plate item ids seen the last time the dresser was open, per character, so the dresser rule has data before plates reload.</summary>
    public Dictionary<ulong, List<uint>> LastKnownPlateItems { get; set; } = new();

    public event Action? Saved;

    /// <summary>Brings a config written by an older build up to date. Returns whether anything changed, so the caller saves.</summary>
    public bool Migrate() => ConfigMigrations.Migrate(this);

    // What the migration steps see. Explicit, so none of it is written to the settings file a second time.
    IMigratableAutomation IMigratableSettings.Automation => Automation;
    IMigratableLayouts IMigratableSettings.Organizer => Organizer;
    int IMigratableSettings.ActionTimeoutMs
    {
        get => Callbacks.ActionTimeoutMs;
        set => Callbacks.ActionTimeoutMs = value;
    }

    public void Save(Dalamud.Plugin.IDalamudPluginInterface pi)
    {
        Windows.Ui.Reduced = ReduceMotion;
        pi.SavePluginConfig(this);
        Saved?.Invoke();
    }
}
