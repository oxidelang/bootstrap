using System;
using System.Text;

namespace Oxide.Compiler.IR.TypeRefs
{
    public class GenericTypeRef : BaseTypeRef
    {
        public string Name { get; }

        public GenericTypeRef(string name)
        {
            Name = name;
        }

        protected bool Equals(GenericTypeRef other)
        {
            return Equals(Name, other.Name);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((GenericTypeRef)obj);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine("generic", Name);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("[g#");
            sb.Append(Name);
            sb.Append("]");

            return sb.ToString();
        }
    }
}