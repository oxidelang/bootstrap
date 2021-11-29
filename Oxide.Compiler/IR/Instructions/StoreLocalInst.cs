using System;
using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Instructions
{
    public class StoreLocalInst : Instruction
    {
        public int ValueId { get; init; }

        public int LocalId { get; init; }

        public override bool HasValue => false;
        public override TypeRef ValueType => throw new InvalidOperationException("No value");

        public override void WriteIr(IrWriter writer)
        {
            writer.Write($"storelocal %{ValueId} ${LocalId}");
        }
    }
}