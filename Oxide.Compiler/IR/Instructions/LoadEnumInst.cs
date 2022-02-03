using System.Collections.Immutable;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class LoadEnumInst : Instruction
{
    public int TargetSlot { get; init; }

    public QualifiedName EnumName { get; init; }

    public string ItemName { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write($"loadenum ${TargetSlot} {EnumName} {ItemName}");
    }

    public override InstructionEffects GetEffects(IrStore store)
    {
        return new InstructionEffects(
            ImmutableArray<InstructionEffects.ReadData>.Empty,
            new[]
            {
                InstructionEffects.WriteData.New(TargetSlot)
            }.ToImmutableArray()
        );
    }
}