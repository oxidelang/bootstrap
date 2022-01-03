namespace Oxide.Compiler.IR.Instructions;

public class SlotBorrowInst : Instruction
{
    public int TargetSlot { get; init; }

    public int BaseSlot { get; init; }

    public bool Mutable { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write($"slotborrow ${TargetSlot} ${BaseSlot} {(Mutable ? "mut" : "readonly")}");
    }
}