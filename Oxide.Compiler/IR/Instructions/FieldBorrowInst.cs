using System.Collections.Immutable;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class FieldBorrowInst : Instruction
{
    public int TargetSlot { get; init; }

    public int BaseSlot { get; init; }

    public string TargetField { get; init; }

    public bool Mutable { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write($"fieldborrow ${TargetSlot} ${BaseSlot} {TargetField} {(Mutable ? "mut" : "readonly")}");
    }

    public override InstructionEffects GetEffects(IrStore store)
    {
        return new InstructionEffects(
            new[]
            {
                InstructionEffects.ReadData.AccessField(BaseSlot, false, TargetField)
            }.ToImmutableArray(),
            new[]
            {
                InstructionEffects.WriteData.Field(TargetSlot, BaseSlot, TargetField, Mutable)
            }.ToImmutableArray()
        );
    }
}