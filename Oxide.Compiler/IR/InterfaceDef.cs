using System.Collections.Immutable;

namespace Oxide.Compiler.IR
{
    public class InterfaceDef
    {
        public Visibility Visibility { get; }

        public QualifiedName Name { get; }

        public ImmutableList<string> GenericParams { get; }
    }
}