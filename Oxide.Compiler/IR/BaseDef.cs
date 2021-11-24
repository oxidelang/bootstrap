using System.Collections.Immutable;

namespace Oxide.Compiler.IR
{
    public abstract class BaseDef
    {
        public QualifiedName Name { get; init; }

        public Visibility Visibility { get; init; }

        public ImmutableList<string> GenericParams { get; init; }
    }
}