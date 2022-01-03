namespace Oxide.Compiler.IR.Instructions;

public class MoveInst : Instruction
{
    public int SrcSlot { get; init; }

    public int DestSlot { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write($"move ${DestSlot} ${SrcSlot}");
    }
}