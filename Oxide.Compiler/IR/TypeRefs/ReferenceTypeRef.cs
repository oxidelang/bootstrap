using System;
using System.Text;

namespace Oxide.Compiler.IR.TypeRefs
{
    public class ReferenceTypeRef : TypeRef
    {
        public bool StrongRef { get; }
        public TypeRef InnerType { get; }

        public ReferenceTypeRef(TypeRef inner, bool strongRef)
        {
            InnerType = inner;
            StrongRef = strongRef;
        }

        public override BaseTypeRef GetBaseType()
        {
            return InnerType.GetBaseType();
        }

        protected bool Equals(ReferenceTypeRef other)
        {
            return StrongRef == other.StrongRef && Equals(InnerType, other.InnerType);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((ReferenceTypeRef)obj);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine("reference", StrongRef, InnerType);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("[r");
            sb.Append(StrongRef ? "s" : "w");
            sb.Append("#");
            sb.Append(InnerType);
            sb.Append("]");

            return sb.ToString();
        }
    }
}