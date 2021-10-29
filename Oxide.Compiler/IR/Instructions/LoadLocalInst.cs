namespace Oxide.Compiler.IR.Instructions
{
    public class LoadLocalInst : Instruction
    {
        public override bool HasValue => true;
        public override TypeDef ValueType => LocalType;
        public int LocalId { get; init; }
        public TypeDef LocalType { get; init; }

        public override void WriteIr(IrWriter writer)
        {
            writer.Write($"loadlocal ${LocalId}");
        }
    }
}