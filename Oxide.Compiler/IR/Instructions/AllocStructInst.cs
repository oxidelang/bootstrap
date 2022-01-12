using System.Collections.Immutable;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class AllocStructInst : Instruction
{
    public int SlotId { get; init; }

    public ConcreteTypeRef StructType { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write($"allocstruct ${SlotId} {StructType}");
    }

    public override InstructionEffects GetEffects(IrStore store)
    {
        return new InstructionEffects(
            ImmutableArray<InstructionEffects.ReadData>.Empty,
            new[]
            {
                InstructionEffects.WriteData.New(SlotId)
            }.ToImmutableArray()
        );
    }
}