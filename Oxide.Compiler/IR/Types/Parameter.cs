using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Types
{
    public class Parameter
    {
        public bool IsThis { get; init; }

        public string Name { get; init; }

        public TypeRef Type { get; init; }
    }
}