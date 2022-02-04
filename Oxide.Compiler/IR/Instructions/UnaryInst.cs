using System.Collections.Immutable;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class UnaryInst : Instruction
{
    public enum Operation
    {
        Not
    }

    public int ResultSlot { get; init; }
    public int Value { get; init; }
    public Operation Op { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write($"unary ${ResultSlot} {Op.ToString().ToLower()} ${Value}");
    }

    public override InstructionEffects GetEffects(IrStore store)
    {
        return new InstructionEffects(
            new[]
            {
                InstructionEffects.ReadData.Access(Value, false)
            }.ToImmutableArray(),
            new[]
            {
                InstructionEffects.WriteData.New(ResultSlot)
            }.ToImmutableArray()
        );
    }
}