using System.Collections.Immutable;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware;

namespace Oxide.Compiler.IR
{
    public class ResolvedFunction
    {
        public ConcreteTypeRef Interface { get; init; }

        public ImmutableDictionary<string, TypeRef> InterfaceGenerics { get; init; }

        public Function Function { get; init; }
    }
}