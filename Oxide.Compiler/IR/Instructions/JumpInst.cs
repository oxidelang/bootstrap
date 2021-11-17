using System;

namespace Oxide.Compiler.IR.Instructions
{
    public class JumpInst : Instruction
    {
        public int TargetBlock { get; init; }
        public int ElseBlock { get; init; }
        public override bool HasValue => false;
        public override TypeDef ValueType => throw new Exception("No value");
        public override bool Terminal => true;

        public int? ConditionValue { get; init; }

        public override void WriteIr(IrWriter writer)
        {
            writer.Write(ConditionValue.HasValue
                ? $"jump %{ConditionValue.Value} #{TargetBlock} #{ElseBlock}"
                : $"jump always #{TargetBlock}");
        }
    }
}