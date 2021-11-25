using System;

namespace Oxide.Compiler.IR.Instructions
{
    public class StoreFieldInst : Instruction
    {
        public int ValueId { get; init; }

        public int TargetId { get; init; }

        public QualifiedName TargetType { get; init; }

        public string TargetField { get; init; }

        public override bool HasValue => false;
        public override TypeRef ValueType => throw new InvalidOperationException("No value");

        public override void WriteIr(IrWriter writer)
        {
            writer.Write($"storefield %{ValueId} %{TargetId} {TargetType} {TargetField}");
        }
    }
}