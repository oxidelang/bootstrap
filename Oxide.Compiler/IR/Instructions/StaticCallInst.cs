using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Instructions
{
    public class StaticCallInst : Instruction
    {
        public BaseTypeRef TargetType { get; init; }

        public QualifiedName TargetImplementation { get; init; }

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

            if (TargetType != null)
            {
                writer.WriteType(TargetType);
                writer.Write(" ");

                if (TargetImplementation != null)
                {
                    writer.WriteQn(TargetImplementation);
                    writer.Write(" ");
                }
            }

            writer.WriteQn(TargetMethod);
            writer.Write($" ({string.Join(", ", Arguments.Select(x => $"${x}"))})");
        }
    }
}