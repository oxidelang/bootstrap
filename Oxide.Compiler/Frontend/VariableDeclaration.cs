using Oxide.Compiler.IR;

namespace Oxide.Compiler.Frontend
{
    public class VariableDeclaration
    {
        public int Id { get; init; }

        public string Name { get; init; }

        public TypeDef Type { get; init; }

        public bool Mutable { get; init; }
    }
}