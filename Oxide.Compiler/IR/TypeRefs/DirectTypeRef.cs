using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

namespace Oxide.Compiler.IR.TypeRefs
{
    public class DirectTypeRef : TypeRef
    {
        public override TypeCategory Category => TypeCategory.Direct;
        public override QualifiedName Name { get; }
        public override TypeSource Source { get; }
        public override ImmutableArray<TypeRef> GenericParams { get; }

        public DirectTypeRef(QualifiedName qn, TypeSource source, ImmutableArray<TypeRef> genericParams)
        {
            Name = qn;
            Source = source;
            GenericParams = genericParams;
        }

        protected bool Equals(DirectTypeRef other)
        {
            return Equals(Name, other.Name) && Source == other.Source &&
                   ((IStructuralEquatable)GenericParams).Equals(other.GenericParams, EqualityComparer<TypeRef>.Default);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((DirectTypeRef)obj);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Name, (int)Source,
                ((IStructuralEquatable)GenericParams).GetHashCode(EqualityComparer<TypeRef>.Default));
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("[d");

            switch (Source)
            {
                case TypeSource.Concrete:
                    sb.Append("c");
                    break;
                case TypeSource.Generic:
                    sb.Append("g");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            sb.Append("]");
            sb.Append(Name);

            return sb.ToString();
        }
    }
}