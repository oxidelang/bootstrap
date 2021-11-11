using System;
using System.Collections.Immutable;
using System.Linq;

namespace Oxide.Compiler.IR.Instructions
{
    public class StaticCallInst : Instruction
    {
        public override bool HasValue => ReturnType != null;
        public override TypeDef ValueType => ReturnType;
        public QualifiedName TargetMethod { get; init; }

        public ImmutableList<int> Arguments { get; init; }

        public TypeDef ReturnType { get; init; }

        public override void WriteIr(IrWriter writer)
        {
            writer.Write("staticcall ");
            writer.WriteQn(TargetMethod);
            writer.Write($" ({string.Join(", ", Arguments.Select(x => $"%{x}"))}) -> ");
            if (ReturnType != null)
            {
                writer.WriteType(ReturnType);
            }
            else
            {
                writer.Write("void");
            }
        }
    }
}