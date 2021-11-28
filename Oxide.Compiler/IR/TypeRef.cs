using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

namespace Oxide.Compiler.IR
{
    public class TypeRef
    {
        public TypeCategory Category { get; init; }

        public bool MutableRef { get; init; }

        public QualifiedName Name { get; init; }

        public TypeSource Source { get; init; }

        public ImmutableArray<TypeRef> GenericParams { get; init; }

        protected bool Equals(TypeRef other)
        {
            return Category == other.Category && MutableRef == other.MutableRef && Name.Equals(other.Name) &&
                   Source == other.Source &&
                   ((IStructuralEquatable)GenericParams).Equals(other.GenericParams, EqualityComparer<TypeRef>.Default);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((TypeRef)obj);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine((int)Category, MutableRef, Name, (int)Source,
                ((IStructuralEquatable)GenericParams).GetHashCode(EqualityComparer<TypeRef>.Default));
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("[");

            sb.Append(MutableRef ? "m" : "_");

            switch (Category)
            {
                case TypeCategory.Direct:
                    sb.Append("d");
                    break;
                case TypeCategory.Pointer:
                    sb.Append("p");
                    break;
                case TypeCategory.Reference:
                    sb.Append("r");
                    break;
                case TypeCategory.StrongReference:
                    sb.Append("s");
                    break;
                case TypeCategory.WeakReference:
                    sb.Append("w");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

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