namespace Oxide.Compiler.IR
{
    public class FieldDef
    {
        public string Name { get; init; }

        public Visibility Visibility { get; init; }

        public TypeDef Type { get; init; }
    }
}