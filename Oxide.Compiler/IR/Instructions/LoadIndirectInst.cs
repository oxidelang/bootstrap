namespace Oxide.Compiler.IR.Instructions
{
    public class LoadIndirectInst : Instruction
    {
        public int TargetSlot { get; init; }

        public int AddressSlot { get; init; }

        public override void WriteIr(IrWriter writer)
        {
            writer.Write($"loadindirect ${TargetSlot} ${AddressSlot}");
        }
    }
}