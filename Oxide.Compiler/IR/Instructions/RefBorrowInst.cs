using System.Collections.Immutable;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class RefBorrowInst : Instruction
{
    public int ResultSlot { get; init; }
    public int SourceSlot { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write($"refborrow ${ResultSlot} ${SourceSlot}");
    }

    public override InstructionEffects GetEffects(IrStore store)
    {
        return new InstructionEffects(
            new[]
            {
                InstructionEffects.ReadData.Access(SourceSlot, false)
            }.ToImmutableArray(),
            new[]
            {
                InstructionEffects.WriteData.Borrow(ResultSlot, SourceSlot, false)
            }.ToImmutableArray()
        );
    }
}