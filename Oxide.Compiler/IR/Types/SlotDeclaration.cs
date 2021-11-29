using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Types
{
    public class SlotDeclaration
    {
        public int Id { get; init; }

        public string Name { get; init; }

        public TypeRef Type { get; init; }

        public bool Mutable { get; init; }

        public int? ParameterSource { get; init; }
    }
}