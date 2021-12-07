using System.Text;

namespace Oxide.Compiler.IR.TypeRefs
{
    public class ThisTypeRef : BaseTypeRef
    {
        protected bool Equals(ThisTypeRef other)
        {
            return true;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((ThisTypeRef)obj);
        }

        public override int GetHashCode()
        {
            return 0;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("[this]");
            return sb.ToString();
        }
    }
}