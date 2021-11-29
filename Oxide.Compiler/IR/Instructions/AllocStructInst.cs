using System;
using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Instructions
{
    public class AllocStructInst : Instruction
    {
        public int LocalId { get; init; }

        public override bool HasValue => false;
        public override TypeRef ValueType => throw new InvalidOperationException("No value");

        public QualifiedName StructName { get; init; }

        public override void WriteIr(IrWriter writer)
        {
            writer.Write($"allocstruct ${LocalId} {StructName}");
        }
    }
}