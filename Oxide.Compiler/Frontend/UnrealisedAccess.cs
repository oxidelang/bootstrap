using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.Frontend
{
    public abstract class UnrealisedAccess
    {
        public abstract TypeRef Type { get; }

        public abstract SlotDeclaration GenerateMove(BodyParser parser, Block block);

        public abstract SlotDeclaration GenerateRef(BodyParser parser, Block block, bool mutable);
    }
}