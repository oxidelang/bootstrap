namespace Oxide.Compiler.IR.Instructions
{
    public abstract class Instruction
    {
        public int Id { get; init; }

        public abstract bool HasValue { get; }

        public abstract TypeDef ValueType { get; }

        public virtual bool Terminal => false;

        public abstract void WriteIr(IrWriter writer);
    }
}