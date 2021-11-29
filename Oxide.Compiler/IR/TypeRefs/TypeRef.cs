using System.Collections.Immutable;

namespace Oxide.Compiler.IR.TypeRefs
{
    public abstract class TypeRef
    {
        public abstract TypeCategory Category { get; }

        public abstract QualifiedName Name { get; }

        public abstract TypeSource Source { get; }

        public abstract ImmutableArray<TypeRef> GenericParams { get; }
    }
}