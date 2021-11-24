using System;

namespace Oxide.Compiler.IR.Instructions
{
    public class AllocStructInst : Instruction
    {
        public int LocalId { get; init; }

        public override bool HasValue => false;
        public override TypeDef ValueType => throw new InvalidOperationException("No value");

        public QualifiedName StructName { get; init; }

        public override void WriteIr(IrWriter writer)
        {
            writer.Write($"allocstruct ${LocalId} {StructName}");
        }
    }
}