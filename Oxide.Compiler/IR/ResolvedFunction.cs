using System.Collections.Immutable;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.IR
{
    public class ResolvedFunction
    {
        public ConcreteTypeRef Interface { get; init; }

        public ImmutableDictionary<string, TypeRef> ImplementationGenerics { get; init; }

        public Function Function { get; init; }
    }
}