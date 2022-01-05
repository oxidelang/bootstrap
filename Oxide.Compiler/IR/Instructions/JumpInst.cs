using System.Collections.Generic;
using System.Collections.Immutable;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class JumpInst : Instruction
{
    public int TargetBlock { get; init; }
    public int ElseBlock { get; init; }
    public override bool Terminal => true;

    public int? ConditionSlot { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write(ConditionSlot.HasValue
            ? $"jump ${ConditionSlot.Value} #{TargetBlock} #{ElseBlock}"
            : $"jump always #{TargetBlock}");
    }

    public override InstructionEffects GetEffects()
    {
        var reads = new List<InstructionEffects.ReadData>();
        var jumps = new List<int> { TargetBlock };

        if (ConditionSlot.HasValue)
        {
            reads.Add(InstructionEffects.ReadData.Access(ConditionSlot.Value, false));
            jumps.Add(ElseBlock);
        }

        return new InstructionEffects(
            reads.ToImmutableArray(),
            ImmutableArray<InstructionEffects.WriteData>.Empty,
            jumps.ToImmutableArray()
        );
    }
}