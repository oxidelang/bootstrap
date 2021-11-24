using System.Collections.Immutable;

namespace Oxide.Compiler.IR
{
    public class VariantDef : BaseDef
    {
        public ImmutableDictionary<string, VariantItemDef> Items { get; init; }
    }
}