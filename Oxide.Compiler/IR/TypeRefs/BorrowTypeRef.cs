using System;
using System.Collections.Immutable;
using System.Text;

namespace Oxide.Compiler.IR.TypeRefs
{
    public class BorrowTypeRef : TypeRef
    {
        public override TypeCategory Category => TypeCategory.Borrow;
        public override QualifiedName Name => InnerType.Name;
        public override TypeSource Source => InnerType.Source;
        public override ImmutableArray<TypeRef> GenericParams => InnerType.GenericParams;

        public bool MutableRef { get; }

        public TypeRef InnerType { get; }

        public BorrowTypeRef(TypeRef inner, bool mutableRef)
        {
            InnerType = inner;
            MutableRef = mutableRef;
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
            return HashCode.Combine(MutableRef, InnerType);
        }


        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("[b");
            sb.Append(MutableRef ? "m" : "r");
            sb.Append("]");
            sb.Append(InnerType);

            return sb.ToString();
        }
    }
}