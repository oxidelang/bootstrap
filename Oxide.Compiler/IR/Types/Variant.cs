using System.Collections.Immutable;

namespace Oxide.Compiler.IR.Types
{
    public class Variant : OxType
    {
        public ImmutableDictionary<string, VariantItem> Items { get; init; }
    }
}