using System.Collections.Generic;
using System.Collections.Immutable;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class ReturnInst : Instruction
{
    public int? ReturnSlot { get; init; }

    public override bool Terminal => true;

    public override void WriteIr(IrWriter writer)
    {
        writer.Write(ReturnSlot.HasValue ? $"return ${ReturnSlot}" : "return void");
    }

    public override InstructionEffects GetEffects()
    {
        var reads = new List<InstructionEffects.ReadData>();
        if (ReturnSlot.HasValue)
        {
            reads.Add(InstructionEffects.ReadData.Access(ReturnSlot.Value, true));
        }

        return new InstructionEffects(
            reads.ToImmutableArray(),
            ImmutableArray<InstructionEffects.WriteData>.Empty
        );
    }
}