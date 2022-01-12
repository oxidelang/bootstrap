using System.Collections.Immutable;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class CastInst : Instruction
{
    public int ResultSlot { get; init; }
    public int SourceSlot { get; init; }
    public TypeRef TargetType { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write($"cast ${ResultSlot} ");
        writer.WriteType(TargetType);
        writer.Write($" ${SourceSlot}");
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
                InstructionEffects.WriteData.New(ResultSlot, moveSource: SourceSlot)
            }.ToImmutableArray()
        );
    }
}