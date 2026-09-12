using Gleam.Core.Lists;
using Gleam.Core.Organizer.Model;

namespace Gleam.Core.Settings;

/// <summary>What the pilot does with rows it only discovers once a container is open (never-cached retainers, the dresser).</summary>
public enum UnseenRowsMode
{
    /// <summary>Leave them for the next review.</summary>
    Skip = 0,
    // 1 was "ask at every container", since retired. Older settings files can still hold it; a migration step moves them on.
    /// <summary>Apply the same rules and clean what they would have checked by default.</summary>
    Clean = 2,
}

/// <summary>
/// The saved settings the migration steps read and rewrite. The settings class itself has to live in the plugin,
/// where Dalamud saves it under its own type name, so the steps reach it through these instead. Nothing here is
/// written to the settings file: the plugin implements the members that differ from its own explicitly.
/// </summary>
public interface IMigratableSettings
{
    int Version { get; set; }
    ProfileStore Profiles { get; }
    IMigratableAutomation Automation { get; }
    IMigratableLayouts Organizer { get; }

    /// <summary>How long to wait for the game to confirm one action (the callback settings' timeout).</summary>
    int ActionTimeoutMs { get; set; }

    bool UseUniversalis { get; set; }
    bool UseAllaganTools { get; set; }
    bool ActWhenMateriaFails { get; set; }
    bool AdvancedMode { get; set; }
    bool SeenFirstRun { get; set; }
    bool UseClean { get; set; }
    bool UseOrganize { get; set; }
    bool AnsweredOrganizeOffer { get; set; }
    string? LastSeenVersion { get; set; }
}

/// <summary>The hands-free settings the migration steps touch.</summary>
public interface IMigratableAutomation
{
    UnseenRowsMode UnseenRows { get; set; }
    bool VisitContainersWithoutRows { get; set; }
    bool Enabled { get; set; }
    bool VisitRetainers { get; set; }
    bool SellAtVendor { get; set; }
    bool VisitDresser { get; set; }
    bool OpenSaddlebag { get; set; }
    bool VisitGrandCompany { get; set; }
}

/// <summary>The organizer's saved layouts and which one is in use.</summary>
public interface IMigratableLayouts
{
    List<OrganizerPlan> Plans { get; }
    Guid? ActivePlanId { get; set; }
}
