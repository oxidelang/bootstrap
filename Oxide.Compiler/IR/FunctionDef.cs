using System.Collections.Immutable;

namespace Oxide.Compiler.IR
{
    public class FunctionDef : BaseDef
    {
        public ImmutableList<ParameterDef> Parameters { get; init; }

        public TypeDef ReturnType { get; init; }

        public bool IsExtern { get; init; }

        public bool HasBody { get; init; }

        public ImmutableList<Scope> Scopes { get; set; }

        public ImmutableList<Block> Blocks { get; set; }

        public int EntryBlock { get; set; }
    }
}