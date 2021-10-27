namespace Oxide.Compiler.IR.Instructions
{
    public class ConstInst : Instruction
    {
        public enum PrimitiveType
        {
            I32
        }

        public PrimitiveType Type { get; init; }

        public object Value { get; init; }
    }
}