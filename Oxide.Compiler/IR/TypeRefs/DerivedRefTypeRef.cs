using System;
using System.Text;

namespace Oxide.Compiler.IR.TypeRefs;

public class DerivedRefTypeRef : TypeRef
{
    public bool StrongRef { get; }
    public TypeRef InnerType { get; }

    public DerivedRefTypeRef(TypeRef inner, bool strongRef)
    {
        InnerType = inner;
        StrongRef = strongRef;
    }

    public override BaseTypeRef GetBaseType()
    {
        return InnerType.GetBaseType();
    }

    protected bool Equals(DerivedRefTypeRef other)
    {
        return StrongRef == other.StrongRef && Equals(InnerType, other.InnerType);
    }

    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != this.GetType()) return false;
        return Equals((DerivedRefTypeRef)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine("derived", StrongRef, InnerType);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append("[dr");
        sb.Append(StrongRef ? "s" : "w");
        sb.Append("#");
        sb.Append(InnerType);
        sb.Append("]");

        return sb.ToString();
    }
}