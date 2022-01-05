using System.Collections.Immutable;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class StoreIndirectInst : Instruction
{
    public int TargetSlot { get; init; }

    public int ValueSlot { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write($"storeindirect ${TargetSlot} ${ValueSlot}");
    }

    public override InstructionEffects GetEffects()
    {
        return new InstructionEffects(
            new[]
            {
                InstructionEffects.ReadData.Access(TargetSlot, false),
                InstructionEffects.ReadData.Access(ValueSlot, true)
            }.ToImmutableArray(),
            ImmutableArray<InstructionEffects.WriteData>.Empty
        );
    }
}