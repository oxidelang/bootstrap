using System.Collections.Immutable;
using System.Linq;

namespace Oxide.Compiler.IR.Instructions
{
    public class StaticCallInst : Instruction
    {
        public QualifiedName TargetMethod { get; init; }

        public ImmutableList<int> Arguments { get; init; }

        public int? ResultSlot { get; init; }

        public override void WriteIr(IrWriter writer)
        {
            writer.Write("staticcall ");
            if (ResultSlot.HasValue)
            {
                writer.Write($"${ResultSlot} ");
            }

            writer.WriteQn(TargetMethod);
            writer.Write($" ({string.Join(", ", Arguments.Select(x => $"${x}"))})");
        }
    }
}