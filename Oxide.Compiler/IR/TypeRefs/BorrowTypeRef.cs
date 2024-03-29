using System;
using System.Text;

namespace Oxide.Compiler.IR.TypeRefs;

public class BorrowTypeRef : TypeRef
{
    public bool MutableRef { get; }
    public TypeRef InnerType { get; }

    public BorrowTypeRef(TypeRef inner, bool mutableRef)
    {
        InnerType = inner;
        MutableRef = mutableRef;
    }

    public override BaseTypeRef GetBaseType()
    {
        return InnerType.GetBaseType();
    }

    protected bool Equals(BorrowTypeRef other)
    {
        return MutableRef == other.MutableRef && Equals(InnerType, other.InnerType);
    }

    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != this.GetType()) return false;
        return Equals((BorrowTypeRef)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine("borrow", MutableRef, InnerType);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append("[b");
        sb.Append(MutableRef ? "m" : "r");
        sb.Append("#");
        sb.Append(InnerType);
        sb.Append("]");

        return sb.ToString();
    }

    public override string ToPrettyString()
    {
        var sb = new StringBuilder();
        sb.Append("&");
        sb.Append(MutableRef ? "mut " : "");
        sb.Append(InnerType.ToPrettyString());
        return sb.ToString();
    }
}