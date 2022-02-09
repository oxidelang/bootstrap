using System;
using System.Text;
using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.Middleware.Usage;

public class FunctionRef
{
    public ConcreteTypeRef TargetType { get; init; }

    public ConcreteTypeRef TargetImplementation { get; init; }

    public ConcreteTypeRef TargetMethod { get; init; }

    protected bool Equals(FunctionRef other)
    {
        return Equals(TargetType, other.TargetType) &&
               Equals(TargetImplementation, other.TargetImplementation) &&
               Equals(TargetMethod, other.TargetMethod);
    }

    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != this.GetType()) return false;
        return Equals((FunctionRef)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(TargetType, TargetImplementation, TargetMethod);
    }

    public override string ToString()
    {
        return
            $"{nameof(TargetType)}: {TargetType}, {nameof(TargetImplementation)}: {TargetImplementation}, {nameof(TargetMethod)}: {TargetMethod}";
    }

    public string ToPrettyString()
    {
        var sb = new StringBuilder();
        if (TargetImplementation != null)
        {
            sb.Append('<');
            sb.Append(TargetType.ToPrettyString());
            sb.Append(" as ");
            sb.Append(TargetImplementation.ToPrettyString());
            sb.Append(">::");
        }
        else if (TargetType != null)
        {
            sb.Append(TargetType.ToPrettyString());
            sb.Append("::");
        }

        sb.Append(TargetMethod.ToPrettyString());

        return sb.ToString();
    }
}