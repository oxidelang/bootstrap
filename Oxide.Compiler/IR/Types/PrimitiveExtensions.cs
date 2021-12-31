using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Types;

public static class PrimitiveExtensions
{
    public static ConcreteTypeRef GetRef(this PrimitiveKind kind)
    {
        return PrimitiveType.GetRef(kind);
    }
}