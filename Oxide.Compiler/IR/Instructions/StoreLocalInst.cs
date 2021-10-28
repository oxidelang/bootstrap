using System;

namespace Oxide.Compiler.IR.Instructions
{
    public class StoreLocalInst : Instruction
    {
        public int ValueId { get; init; }

        public int LocalId { get; init; }

        public override bool HasValue => false;
        public override TypeDef ValueType => throw new InvalidOperationException("No value");
    }
}