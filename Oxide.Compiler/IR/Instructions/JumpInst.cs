namespace Oxide.Compiler.IR.Instructions
{
    public class JumpInst : Instruction
    {
        public int TargetBlock { get; init; }
        public int ElseBlock { get; init; }
        public override bool Terminal => true;

        public int? ConditionSlot { get; init; }

        public override void WriteIr(IrWriter writer)
        {
            writer.Write(ConditionSlot.HasValue
                ? $"jump ${ConditionSlot.Value} #{TargetBlock} #{ElseBlock}"
                : $"jump always #{TargetBlock}");
        }
    }
}