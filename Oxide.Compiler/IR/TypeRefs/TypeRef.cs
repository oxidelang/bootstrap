namespace Oxide.Compiler.IR.TypeRefs;

/// <summary>
/// Represents a Oxide type by its name or off of another type
/// </summary>
public abstract class TypeRef
{
    public virtual bool IsBaseType => false;

    public abstract BaseTypeRef GetBaseType();

    public abstract string ToPrettyString();
}