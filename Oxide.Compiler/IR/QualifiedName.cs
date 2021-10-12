using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Oxide.Compiler.IR
{
    public class QualifiedName
    {
        public bool IsAbsolute { get; }

        public ImmutableArray<string> Parts { get; }

        public QualifiedName(bool isAbsolute, IEnumerable<string> parts)
        {
            IsAbsolute = isAbsolute;
            Parts = parts.ToImmutableArray();
        }

        protected bool Equals(QualifiedName other)
        {
            return IsAbsolute == other.IsAbsolute && Parts.Equals(other.Parts);
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
            return HashCode.Combine(IsAbsolute, Parts);
        }

        public override string ToString()
        {
            return (IsAbsolute ? "::" : "") + string.Join("::", Parts);
        }
    }
}