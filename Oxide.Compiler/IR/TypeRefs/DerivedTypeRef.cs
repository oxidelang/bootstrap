using System;
using System.Text;

namespace Oxide.Compiler.IR.TypeRefs;

public class DerivedTypeRef : BaseTypeRef
{
    public TypeRef BaseRef { get; }
    public ConcreteTypeRef CastType { get; }
    public string Target { get; }

    public DerivedTypeRef(TypeRef baseRef, ConcreteTypeRef caseType, string target)
    {
        BaseRef = baseRef;
        CastType = caseType;
        Target = target;
    }

    protected bool Equals(DerivedTypeRef other)
    {
        return Equals(BaseRef, other.BaseRef) && Equals(CastType, other.CastType) && Equals(Target, other.Target);
    }

    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != this.GetType()) return false;
        return Equals((DerivedTypeRef)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine("derived", BaseRef, CastType, Target);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append("[d#");
        sb.Append(BaseRef);
        sb.Append("#");
        sb.Append(CastType);
        sb.Append("#");
        sb.Append(Target);
        sb.Append("]");
        return sb.ToString();
    }

    public override string ToPrettyString()
    {
        var sb = new StringBuilder();
        sb.Append(BaseRef.ToPrettyString());
        sb.Append("::");
        sb.Append(CastType.ToPrettyString());
        sb.Append("::");
        sb.Append(Target);
        return sb.ToString();
    }
}