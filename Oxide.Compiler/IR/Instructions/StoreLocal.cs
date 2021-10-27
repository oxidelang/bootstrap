namespace Oxide.Compiler.IR.Instructions
{
    public class StoreLocal : Instruction
    {
        public int ValueId { get; init; }

        public int TargetId { get; init; }

        public override bool HasValue => false;
        public override TypeDef ValueType => null;
    }
}