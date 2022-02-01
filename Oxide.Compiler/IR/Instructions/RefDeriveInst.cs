using System.Collections.Immutable;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class RefDeriveInst : Instruction
{
    public int ResultSlot { get; init; }
    public int SourceSlot { get; init; }
    public string FieldName { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write($"refderive ${ResultSlot} ${SourceSlot} {FieldName ?? "base"}");
    }

    public override InstructionEffects GetEffects(IrStore store)
    {
        return new InstructionEffects(
            new[]
            {
                InstructionEffects.ReadData.Access(SourceSlot, true)
            }.ToImmutableArray(),
            new[]
            {
                InstructionEffects.WriteData.New(ResultSlot)
            }.ToImmutableArray()
        );
    }
}