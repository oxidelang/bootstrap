using System.Collections.Immutable;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class MoveInst : Instruction
{
    public int SrcSlot { get; init; }

    public int DestSlot { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write($"move ${DestSlot} ${SrcSlot}");
    }

    public override InstructionEffects GetEffects(IrStore store)
    {
        return new InstructionEffects(
            new[]
            {
                InstructionEffects.ReadData.Access(SrcSlot, true)
            }.ToImmutableArray(),
            new[]
            {
                InstructionEffects.WriteData.New(DestSlot, moveSource: SrcSlot)
            }.ToImmutableArray()
        );
    }
}