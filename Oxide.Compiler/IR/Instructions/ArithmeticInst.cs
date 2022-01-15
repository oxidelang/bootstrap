using System;
using System.Collections.Immutable;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class ArithmeticInst : Instruction
{
    public enum Operation
    {
        Add,
        Minus,
        LogicalAnd,
        LogicalOr,
    }

    public int ResultSlot { get; init; }
    public int LhsValue { get; init; }
    public int RhsValue { get; init; }
    public Operation Op { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        var op = Op switch
        {
            Operation.Add => "add",
            Operation.Minus => "minus",
            Operation.LogicalAnd => "logicaland",
            Operation.LogicalOr => "logicalor",
            _ => throw new ArgumentOutOfRangeException()
        };

        writer.Write($"arithmetic ${ResultSlot} {op} ${LhsValue} ${RhsValue}");
    }

    public override InstructionEffects GetEffects(IrStore store)
    {
        return new InstructionEffects(
            new[]
            {
                InstructionEffects.ReadData.Access(LhsValue, false),
                InstructionEffects.ReadData.Access(RhsValue, false)
            }.ToImmutableArray(),
            new[]
            {
                InstructionEffects.WriteData.New(ResultSlot)
            }.ToImmutableArray()
        );
    }
}