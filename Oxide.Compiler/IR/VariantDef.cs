using System.Collections.Immutable;

namespace Oxide.Compiler.IR
{
    public class VariantDef
    {
        public QualifiedName Name { get; init; }

        public Visibility Visibility { get; init; }

        public ImmutableList<string> GenericParams { get; init; }

        public ImmutableDictionary<string, VariantItemDef> Items { get; init; }
    }
}