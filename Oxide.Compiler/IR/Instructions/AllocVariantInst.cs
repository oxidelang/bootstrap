using System.Collections.Generic;
using System.Collections.Immutable;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class AllocVariantInst : Instruction
{
    public int SlotId { get; init; }

    public ConcreteTypeRef VariantType { get; init; }

    public string ItemName { get; init; }

    public int? ItemSlot { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write($"allocvariant ${SlotId} {VariantType} {ItemName} ${ItemSlot}");
    }

    public override InstructionEffects GetEffects(IrStore store)
    {
        var reads = new List<InstructionEffects.ReadData>();
        if (ItemSlot.HasValue)
        {
            reads.Add(InstructionEffects.ReadData.Access(ItemSlot.Value, true));
        }

        return new InstructionEffects(
            reads.ToImmutableArray(),
            new[]
            {
                InstructionEffects.WriteData.New(SlotId)
            }.ToImmutableArray()
        );
    }
}