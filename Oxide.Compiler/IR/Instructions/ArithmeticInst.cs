namespace Oxide.Compiler.IR.Instructions
{
    public class ArithmeticInst : Instruction
    {
        public enum Operation
        {
            Add,
            Minus
        }

        public override bool HasValue => true;
        public override TypeDef ValueType => OutputType;
        public TypeDef OutputType { get; init; }
        public int LhsValue { get; init; }
        public int RhsValue { get; init; }
        public Operation Op { get; init; }
    }
}