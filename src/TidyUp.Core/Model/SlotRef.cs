namespace TidyUp.Core.Model;

/// <summary>
/// Addresses one physical slot. <see cref="ContainerId"/> is the raw game InventoryType value
/// (or <see cref="GameContainerIds.GlamourDresser"/>), <see cref="Slot"/> is the slot index
/// (prism box index for the dresser), and <see cref="OwnerId"/> is the retainer id for retainer
/// pages, otherwise 0 for the player.
/// </summary>
public readonly record struct SlotRef(ContainerKind Kind, uint ContainerId, int Slot, ulong OwnerId = 0)
{
    public static SlotRef Dresser(int prismBoxIndex) =>
        new(ContainerKind.GlamourDresser, GameContainerIds.GlamourDresser, prismBoxIndex);

    public override string ToString() => OwnerId == 0
        ? $"{Kind}:{ContainerId}#{Slot}"
        : $"{Kind}:{ContainerId}#{Slot}@{OwnerId:X}";
}
