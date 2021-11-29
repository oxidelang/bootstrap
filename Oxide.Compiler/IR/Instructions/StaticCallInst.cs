using System;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Instructions
{
    public class StaticCallInst : Instruction
    {
        public override bool HasValue => false;
        public override TypeRef ValueType => throw new InvalidOperationException();
        public QualifiedName TargetMethod { get; init; }

        public ImmutableList<int> Arguments { get; init; }

        public int? ResultLocal { get; init; }

        public override void WriteIr(IrWriter writer)
        {
            writer.Write("staticcall ");
            writer.WriteQn(TargetMethod);
            writer.Write($" ({string.Join(", ", Arguments.Select(x => $"%{x}"))})");
            if (ResultLocal.HasValue)
            {
                writer.Write($"-> ${ResultLocal}");   
            }
        }
    }
}