namespace Oxide.Compiler.IR.Instructions
{
    public class StoreIndirectInst : Instruction
    {
        public int TargetSlot { get; init; }

        public int ValueSlot { get; init; }

        public override void WriteIr(IrWriter writer)
        {
            writer.Write($"storeindirect ${TargetSlot} ${ValueSlot}");
        }
    }
}