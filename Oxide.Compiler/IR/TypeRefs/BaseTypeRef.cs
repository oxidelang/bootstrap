namespace Oxide.Compiler.IR.TypeRefs;

public abstract class BaseTypeRef : TypeRef
{
    public override bool IsBaseType => true;

    public override BaseTypeRef GetBaseType()
    {
        return this;
    }
}