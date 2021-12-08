using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.Utils;

namespace Oxide.Compiler.Middleware
{
    public class UsedType
    {
        public QualifiedName Name { get; }

        public HashSet<ImmutableArray<TypeRef>> GenericVersions { get; set; }

        public UsedType(QualifiedName name)
        {
            Name = name;
            GenericVersions = new HashSet<ImmutableArray<TypeRef>>(
                new SequenceEqualityComparer<ImmutableArray<TypeRef>>()
            );
        }

        public void AddGenericVariant(ImmutableArray<TypeRef> types)
        {
            if (types.Length == 0)
            {
                return;
            }

            if (GenericVersions.Add(types))
            {
                Console.WriteLine($" - New variant of {Name}: {string.Join(",", types)}");
            }
        }

        protected bool Equals(UsedType other)
        {
            return Equals(Name, other.Name);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((UsedType)obj);
        }

        public override int GetHashCode()
        {
            return Name.GetHashCode();
        }
    }
}