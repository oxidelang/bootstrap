using System.Collections.Immutable;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class LoadIndirectInst : Instruction
{
    public int TargetSlot { get; init; }

    public int AddressSlot { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write($"loadindirect ${TargetSlot} ${AddressSlot}");
    }

    public override InstructionEffects GetEffects()
    {
        return new InstructionEffects(
            new[]
            {
                InstructionEffects.ReadData.Access(AddressSlot, false)
            }.ToImmutableArray(),
            new[]
            {
                InstructionEffects.WriteData.New(TargetSlot)
            }.ToImmutableArray()
        );
    }
}