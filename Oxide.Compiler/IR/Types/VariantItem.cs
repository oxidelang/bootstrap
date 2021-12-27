namespace Oxide.Compiler.IR.Types
{
    public class VariantItem
    {
        public string Name { get; init; }

        public bool NamedFields { get; init; }

        public Struct Content { get; init; }
    }
}