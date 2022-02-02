using System;
using System.Collections.Immutable;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class ConstInst : Instruction
{
    public int TargetSlot { get; init; }

    public PrimitiveKind ConstType { get; init; }

    public object Value { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write($"const ${TargetSlot} {ConstType} {Value}");
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