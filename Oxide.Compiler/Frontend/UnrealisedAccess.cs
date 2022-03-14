using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.Frontend;

/// <summary>
/// Unrealised accesses represent an ambiguous usage of a slot or field. This allows the loading to be deferred until
/// the usage is known and the correct load can be generated, such as taking a reference instead of moving for function
/// calls.
/// </summary>
public abstract class UnrealisedAccess
{
    public abstract TypeRef Type { get; }

    public abstract SlotDeclaration GenerateMove(BodyParser parser, Block block);

    public abstract SlotDeclaration GenerateRef(BodyParser parser, Block block, bool mutable);

    public abstract SlotDeclaration GenerateDerivedRef(BodyParser parser, Block block);
}