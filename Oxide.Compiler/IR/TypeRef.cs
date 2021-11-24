using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

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
            return
                $"{nameof(Category)}: {Category}, {nameof(MutableRef)}: {MutableRef}, {nameof(Name)}: {Name}, {nameof(Source)}: {Source}, {nameof(GenericParams)}: {string.Join(",", GenericParams)}";
        }
    }
}