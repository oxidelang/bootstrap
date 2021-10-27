namespace Oxide.Compiler.IR
{
    public class ParameterDef
    {
        public bool IsThis { get; init; }

        public string Name { get; init; }

        public TypeDef Type { get; init; }
    }
}