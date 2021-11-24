using System;

namespace Oxide.Compiler.IR.Instructions
{
    public class ComparisonInst : Instruction
    {
        public enum Operation
        {
            Eq,
            NEq,
            GEq,
            LEq,
            Gt,
            Lt
        }

        public override bool HasValue => true;
        public override TypeRef ValueType => PrimitiveType.BoolRef;
        public int LhsValue { get; init; }
        public int RhsValue { get; init; }
        public Operation Op { get; init; }

        public override void WriteIr(IrWriter writer)
        {
            var op = Op switch
            {
                Operation.Eq => "eq",
                Operation.NEq => "neq",
                Operation.GEq => "geq",
                Operation.LEq => "leq",
                Operation.Gt => "gt",
                Operation.Lt => "lt",
                _ => throw new ArgumentOutOfRangeException()
            };

            writer.Write($"comparison {op} %{LhsValue} %{RhsValue}");
        }
    }
}