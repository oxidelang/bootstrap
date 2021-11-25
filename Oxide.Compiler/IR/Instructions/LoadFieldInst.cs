using System;

namespace Oxide.Compiler.IR.Instructions
{
    public class LoadFieldInst : Instruction
    {
        public int TargetId { get; init; }

        public QualifiedName TargetType { get; init; }

        public string TargetField { get; init; }

        public TypeRef TargetFieldType { get; init; }

        public override bool HasValue => true;
        public override TypeRef ValueType => TargetFieldType;

        public override void WriteIr(IrWriter writer)
        {
            writer.Write($"loadfield %{TargetId} {TargetType} {TargetField} ");
            writer.WriteType(TargetFieldType);
        }
    }
}