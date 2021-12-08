using System.Collections.Immutable;
using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Types
{
    public class Function : OxObj
    {
        public ImmutableList<Parameter> Parameters { get; init; }

        public TypeRef ReturnType { get; init; }

        public bool IsExtern { get; init; }

        public bool IsExported { get; set; }

        public bool HasBody { get; init; }

        public ImmutableList<Scope> Scopes { get; set; }

        public ImmutableList<Block> Blocks { get; set; }

        public int EntryBlock { get; set; }
    }
}