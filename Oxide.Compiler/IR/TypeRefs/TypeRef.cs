namespace Oxide.Compiler.IR.TypeRefs;

public abstract class TypeRef
{
    public virtual bool IsBaseType => false;

    public abstract BaseTypeRef GetBaseType();

    public abstract string ToPrettyString();
}