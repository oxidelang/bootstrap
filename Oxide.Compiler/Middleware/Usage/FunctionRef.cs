using System;
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
}