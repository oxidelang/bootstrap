namespace Oxide.Compiler.IR.Instructions
{
    public class JumpInst : Instruction
    {
        public int TargetScope { get; init; }
        public override bool HasValue { get; }
        public override TypeDef ValueType { get; }
    }
}