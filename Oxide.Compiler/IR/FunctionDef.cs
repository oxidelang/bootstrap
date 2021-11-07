using System.Collections.Immutable;
using Oxide.Compiler.Parser;

namespace Oxide.Compiler.IR
{
    public class FunctionDef
    {
        public Visibility Visibility { get; init; }

        public QualifiedName Name { get; init; }

        public ImmutableList<string> GenericParams { get; init; }

        public ImmutableList<ParameterDef> Parameters { get; init; }

        public TypeDef ReturnType { get; init; }

        public bool IsExtern { get; init; }

        public bool HasBody { get; init; }

        public OxideParser.BlockContext UnparsedBody { get; set; }

        public ImmutableList<Scope> Scopes { get; set; }

        public ImmutableList<Block> Blocks { get; set; }

        public int EntryBlock { get; set; }
    }
}