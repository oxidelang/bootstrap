using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Instructions;

public class CastInst : Instruction
{
    public int ResultSlot { get; init; }
    public int SourceSlot { get; init; }
    public TypeRef TargetType { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write($"cast ${ResultSlot} ");
        writer.WriteType(TargetType);
        writer.Write($" ${SourceSlot}");
    }
}