using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Instructions;

public class StaticCallInst : Instruction
{
    public BaseTypeRef TargetType { get; init; }

    public ConcreteTypeRef TargetImplementation { get; init; }

    public ConcreteTypeRef TargetMethod { get; init; }

    public ImmutableList<int> Arguments { get; init; }

    public int? ResultSlot { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write("staticcall ");
        if (ResultSlot.HasValue)
        {
            writer.Write($"${ResultSlot} ");
        }

        if (TargetType != null)
        {
            writer.WriteType(TargetType);
            writer.Write(" ");

            if (TargetImplementation != null)
            {
                writer.WriteType(TargetImplementation);
                writer.Write(" ");
            }
        }

        writer.Write($"{TargetMethod} ({string.Join(", ", Arguments.Select(x => $"${x}"))})");
    }
}