using System.Collections.Immutable;

namespace Oxide.Compiler.IR
{
    public class Variant : OxType
    {
        public ImmutableDictionary<string, VariantItem> Items { get; init; }
    }
}