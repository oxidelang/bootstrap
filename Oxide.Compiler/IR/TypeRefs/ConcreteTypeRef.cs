using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

namespace Oxide.Compiler.IR.TypeRefs
{
    public class ConcreteTypeRef : BaseTypeRef
    {
        public QualifiedName Name { get; }
        public ImmutableArray<TypeRef> GenericParams { get; }

        public ConcreteTypeRef(QualifiedName qn, ImmutableArray<TypeRef> genericParams)
        {
            Name = qn;
            GenericParams = genericParams;
        }

        public static ConcreteTypeRef From(QualifiedName qn, params TypeRef[] generics)
        {
            return new ConcreteTypeRef(qn, generics.ToImmutableArray());
        }

        protected bool Equals(ConcreteTypeRef other)
        {
            return Equals(Name, other.Name) &&
                   ((IStructuralEquatable)GenericParams).Equals(other.GenericParams, EqualityComparer<TypeRef>.Default);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((ConcreteTypeRef)obj);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine("concrete", Name,
                ((IStructuralEquatable)GenericParams).GetHashCode(EqualityComparer<TypeRef>.Default));
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("[c#");
            sb.Append(Name);
            if (GenericParams.Length > 0)
            {
                sb.Append('<');
                sb.Append(string.Join(", ", GenericParams));
                sb.Append('>');
            }

            sb.Append("]");

            return sb.ToString();
        }
    }
}