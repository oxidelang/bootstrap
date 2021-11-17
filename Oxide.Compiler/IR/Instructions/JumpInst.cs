using System;

namespace Oxide.Compiler.IR.Instructions
{
    public class JumpInst : Instruction
    {
        public int TargetBlock { get; init; }
        public override bool HasValue => false;
        public override TypeDef ValueType => throw new Exception("No value");
        public override bool Terminal => true;

        public override void WriteIr(IrWriter writer)
        {
            writer.Write($"jump #{TargetBlock}");
        }
    }
}