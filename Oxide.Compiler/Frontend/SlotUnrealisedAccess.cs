using System;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.Frontend;

public class SlotUnrealisedAccess : UnrealisedAccess
{
    public SlotDeclaration Slot { get; }

    public override TypeRef Type => Slot.Type;

    public SlotUnrealisedAccess(SlotDeclaration slot)
    {
        Slot = slot;
    }

    public override SlotDeclaration GenerateMove(BodyParser parser, Block block)
    {
        if (!block.Scope.CanAccessSlot(Slot.Id))
        {
            throw new Exception($"Slot {Slot.Id} not accessible from {block.Id}");
        }

        return Slot;
    }

    public override SlotDeclaration GenerateRef(BodyParser parser, Block block, bool mutable)
    {
        if (!block.Scope.CanAccessSlot(Slot.Id))
        {
            throw new Exception($"Slot {Slot.Id} not accessible from {block.Id}");
        }

        var varSlot = block.Scope.DefineSlot(new SlotDeclaration
        {
            Id = ++parser.LastSlotId,
            Mutable = false,
            Name = null,
            Type = new BorrowTypeRef(Slot.Type, mutable)
        });

        block.AddInstruction(new SlotBorrowInst
        {
            Id = ++parser.LastInstId,
            BaseSlot = Slot.Id,
            Mutable = mutable,
            TargetSlot = varSlot.Id
        });

        return varSlot;
    }
}