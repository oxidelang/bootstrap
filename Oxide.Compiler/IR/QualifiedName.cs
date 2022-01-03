using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Oxide.Compiler.IR;

public class QualifiedName
{
    public bool IsAbsolute { get; }

    public ImmutableArray<string> Parts { get; }

    public QualifiedName(bool isAbsolute, IEnumerable<string> parts)
    {
        IsAbsolute = isAbsolute;
        Parts = parts.ToImmutableArray();
    }

    public static QualifiedName From(params string[] parts)
    {
        return new QualifiedName(true, parts);
    }

    protected bool Equals(QualifiedName other)
    {
        return IsAbsolute == other.IsAbsolute &&
               ((IStructuralEquatable)Parts).Equals(other.Parts, EqualityComparer<string>.Default);
    }

    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != this.GetType()) return false;
        return Equals((QualifiedName)obj);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            IsAbsolute,
            ((IStructuralEquatable)Parts).GetHashCode(EqualityComparer<string>.Default)
        );
    }

    public override string ToString()
    {
        return (IsAbsolute ? "::" : "") + string.Join("::", Parts);
    }
}