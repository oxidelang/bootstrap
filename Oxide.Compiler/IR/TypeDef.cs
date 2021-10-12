using System.Collections.Immutable;

namespace Oxide.Compiler.IR
{
    public class TypeDef
    {
        public TypeCategory Category { get; init; }

        public bool Mutable { get; init; }

        public QualifiedName Name { get; init; }

        public TypeSource Source { get; init; }

        public ImmutableList<TypeDef> GenericParams { get; init; }
    }
}