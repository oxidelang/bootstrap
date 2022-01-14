using System;

namespace Oxide.Compiler.Middleware.Lifetimes;

public class Requirement
{
    public int Value { get; }

    public bool Mutable { get; }

    public string Field { get; }

    public Requirement(int value, bool mutable, string field)
    {
        Value = value;
        Mutable = mutable;
        Field = field;
    }

    protected bool Equals(Requirement other)
    {
        return Value == other.Value && Mutable == other.Mutable && Field == other.Field;
    }

    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != this.GetType()) return false;
        return Equals((Requirement)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Value, Mutable, Field);
    }
}