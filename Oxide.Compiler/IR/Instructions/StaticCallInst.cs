using System;
using System.Collections.Immutable;

namespace Oxide.Compiler.IR.Instructions
{
    public class StaticCallInst : Instruction
    {
        public override bool HasValue => throw new NotImplementedException("HasValue");
        public override TypeDef ValueType => throw new NotImplementedException("ValueType");

        public QualifiedName TargetMethod { get; init; }

        public ImmutableList<int> Arguments { get; init; }
    }
}