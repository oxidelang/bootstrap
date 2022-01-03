namespace Oxide.Compiler.IR.Instructions;

public class ReturnInst : Instruction
{
    public int? ReturnSlot { get; init; }

    public override bool Terminal => true;

    public override void WriteIr(IrWriter writer)
    {
        writer.Write(ReturnSlot.HasValue ? $"return ${ReturnSlot}" : "return void");
    }
}