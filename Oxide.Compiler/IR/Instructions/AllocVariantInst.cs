using Oxide.Compiler.IR.TypeRefs;

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
}