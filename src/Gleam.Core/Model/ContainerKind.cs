namespace Gleam.Core.Model;

/// <summary>The five storage areas Gleam can propose against.</summary>
public enum ContainerKind
{
    Inventory,
    Armoury,
    Saddlebag,
    Retainer,
    GlamourDresser,
}

public static class ContainerKindExtensions
{
    public static string DisplayName(this ContainerKind kind) => kind switch
    {
        ContainerKind.Inventory => "Bags",
        ContainerKind.Armoury => "Armoury chest",
        ContainerKind.Saddlebag => "Chocobo saddlebag",
        ContainerKind.Retainer => "Retainer",
        ContainerKind.GlamourDresser => "Glamour dresser",
        _ => kind.ToString(),
    };

    /// <summary>Containers that are always in memory while logged in.</summary>
    public static bool IsAlwaysLoaded(this ContainerKind kind) =>
        kind is ContainerKind.Inventory or ContainerKind.Armoury;

    /// <summary>Execution order: the dresser needs free inventory slots, so it always runs last.</summary>
    public static int ExecutionOrder(this ContainerKind kind) => kind switch
    {
        ContainerKind.Inventory => 0,
        ContainerKind.Armoury => 1,
        ContainerKind.Saddlebag => 2,
        ContainerKind.Retainer => 3,
        ContainerKind.GlamourDresser => 4,
        _ => 9,
    };

    public static string RequirementText(this ContainerKind kind) => kind switch
    {
        ContainerKind.Saddlebag => "open the chocobo saddlebag",
        ContainerKind.Retainer => "summon this retainer at a bell",
        ContainerKind.GlamourDresser => "open the glamour dresser in an inn room",
        _ => string.Empty,
    };
}

/// <summary>Where a piece of equipment goes on the body, hence which armoury page holds it.</summary>
public enum EquipSlot
{
    None,
    MainHand,
    OffHand,
    Head,
    Body,
    Hands,
    Legs,
    Feet,
    Ears,
    Neck,
    Wrists,
    Ring,
    SoulCrystal,
}

/// <summary>Raw game InventoryType ids, mirrored so Core never references the game assemblies.</summary>
public static class GameContainerIds
{
    public const uint Inventory1 = 0;
    public const uint Inventory2 = 1;
    public const uint Inventory3 = 2;
    public const uint Inventory4 = 3;

    public const uint ArmoryOffHand = 3200;
    public const uint ArmoryHead = 3201;
    public const uint ArmoryBody = 3202;
    public const uint ArmoryHands = 3203;
    public const uint ArmoryLegs = 3205;
    public const uint ArmoryFeets = 3206;
    public const uint ArmoryEar = 3207;
    public const uint ArmoryNeck = 3208;
    public const uint ArmoryWrist = 3209;
    public const uint ArmoryRings = 3300;
    public const uint ArmorySoulCrystal = 3400;
    public const uint ArmoryMainHand = 3500;

    public const uint SaddleBag1 = 4000;
    public const uint SaddleBag2 = 4001;
    public const uint PremiumSaddleBag1 = 4100;
    public const uint PremiumSaddleBag2 = 4101;

    public const uint RetainerPage1 = 10000;
    public const uint RetainerPage7 = 10006;

    /// <summary>Synthetic id used for glamour dresser slots (prism box index lives in <see cref="SlotRef.Slot"/>).</summary>
    public const uint GlamourDresser = 0xFFFF_0001;

    public static readonly uint[] InventoryPages = [Inventory1, Inventory2, Inventory3, Inventory4];

    public static readonly uint[] ArmouryPages =
    [
        ArmoryMainHand, ArmoryOffHand, ArmoryHead, ArmoryBody, ArmoryHands, ArmoryLegs, ArmoryFeets,
        ArmoryEar, ArmoryNeck, ArmoryWrist, ArmoryRings,
    ];

    public static readonly uint[] SaddlebagPages = [SaddleBag1, SaddleBag2, PremiumSaddleBag1, PremiumSaddleBag2];

    public static readonly uint[] RetainerPages =
        Enumerable.Range((int)RetainerPage1, 7).Select(i => (uint)i).ToArray();

    /// <summary>The armoury chest page a piece of equipment belongs to, or 0 when it is not equipment.</summary>
    public static uint ArmouryPageFor(EquipSlot slot) => slot switch
    {
        EquipSlot.MainHand => ArmoryMainHand,
        EquipSlot.OffHand => ArmoryOffHand,
        EquipSlot.Head => ArmoryHead,
        EquipSlot.Body => ArmoryBody,
        EquipSlot.Hands => ArmoryHands,
        EquipSlot.Legs => ArmoryLegs,
        EquipSlot.Feet => ArmoryFeets,
        EquipSlot.Ears => ArmoryEar,
        EquipSlot.Neck => ArmoryNeck,
        EquipSlot.Wrists => ArmoryWrist,
        EquipSlot.Ring => ArmoryRings,
        EquipSlot.SoulCrystal => ArmorySoulCrystal,
        _ => 0,
    };

    public static ContainerKind? KindOf(uint gameContainerId)
    {
        if (gameContainerId <= Inventory4) return ContainerKind.Inventory;
        if (gameContainerId >= ArmoryOffHand && gameContainerId <= ArmoryMainHand && gameContainerId != ArmorySoulCrystal)
            return ContainerKind.Armoury;
        if (gameContainerId >= SaddleBag1 && gameContainerId <= PremiumSaddleBag2) return ContainerKind.Saddlebag;
        if (gameContainerId >= RetainerPage1 && gameContainerId <= RetainerPage7) return ContainerKind.Retainer;
        if (gameContainerId == GlamourDresser) return ContainerKind.GlamourDresser;
        return null;
    }
}
