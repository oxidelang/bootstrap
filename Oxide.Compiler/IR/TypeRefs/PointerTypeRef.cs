using System;
using System.Text;

namespace Oxide.Compiler.IR.TypeRefs;

public class PointerTypeRef : TypeRef
{
    public bool MutableRef { get; }
    public TypeRef InnerType { get; }

    public PointerTypeRef(TypeRef inner, bool mutableRef)
    {
        InnerType = inner;
        MutableRef = mutableRef;
    }

    public override BaseTypeRef GetBaseType()
    {
        return InnerType.GetBaseType();
    }

    protected bool Equals(PointerTypeRef other)
    {
        return MutableRef == other.MutableRef && Equals(InnerType, other.InnerType);
    }

    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != this.GetType()) return false;
        return Equals((PointerTypeRef)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine("pointer", MutableRef, InnerType);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append("[p");
        sb.Append(MutableRef ? "m" : "r");
        sb.Append("#");
        sb.Append(InnerType);
        sb.Append("]");

        return sb.ToString();
    }
}