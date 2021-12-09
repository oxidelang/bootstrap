using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Instructions
{
    public class AllocStructInst : Instruction
    {
        public int SlotId { get; init; }

        public ConcreteTypeRef StructType { get; init; }

        public override void WriteIr(IrWriter writer)
        {
            writer.Write($"allocstruct ${SlotId} {StructType}");
        }
    }
}