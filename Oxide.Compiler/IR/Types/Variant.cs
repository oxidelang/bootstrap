using System.Collections.Immutable;
using System.Linq;

namespace Oxide.Compiler.IR.Types
{
    public class Variant : OxType
    {
        public ImmutableList<VariantItem> Items { get; init; }

        public bool TryGetItem(string name, out VariantItem item)
        {
            item = Items.FirstOrDefault(entry => entry.Name == name);
            return item != null;
        }
    }
}