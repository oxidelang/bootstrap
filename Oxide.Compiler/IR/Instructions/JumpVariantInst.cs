using System.Collections.Immutable;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class JumpVariantInst : Instruction
{
    public int TargetBlock { get; init; }
    public int ElseBlock { get; init; }
    public override bool Terminal => true;

    public int VariantSlot { get; init; }

    public ConcreteTypeRef VariantItemType { get; init; }

    public int ItemSlot { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write(
            $"jumpvariant ${VariantSlot} {VariantItemType} ${ItemSlot} #{TargetBlock} #{ElseBlock}");
    }

    public override InstructionEffects GetEffects(IrStore store)
    {
        return new InstructionEffects(
            new[]
            {
                InstructionEffects.ReadData.Access(VariantSlot, true)
            }.ToImmutableArray(),
            new[]
            {
                InstructionEffects.WriteData.New(ItemSlot, targetBlock: TargetBlock, moveSource: VariantSlot)
            }.ToImmutableArray(),
            new[]
            {
                TargetBlock,
                ElseBlock
            }.ToImmutableArray()
        );
    }
}