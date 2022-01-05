using System.Collections.Immutable;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class FieldMoveInst : Instruction
{
    public int TargetSlot { get; init; }

    public int BaseSlot { get; init; }

    public string TargetField { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write($"fieldmove ${TargetSlot} ${BaseSlot} {TargetField}");
    }

    public override InstructionEffects GetEffects()
    {
        return new InstructionEffects(
            new[]
            {
                InstructionEffects.ReadData.AccessField(BaseSlot, true, TargetField)
            }.ToImmutableArray(),
            new[]
            {
                InstructionEffects.WriteData.New(TargetSlot)
            }.ToImmutableArray()
        );
    }
}