using System;

namespace Oxide.Compiler.IR.TypeRefs
{
    public abstract class TypeRef
    {
        public virtual bool IsBaseType => false;

        public abstract BaseTypeRef GetBaseType();

        public ConcreteTypeRef GetConcreteBaseType()
        {
            var baseType = GetBaseType();
            if (baseType is not ConcreteTypeRef concreteTypeRef)
            {
                throw new Exception("Type has not been resolved to a concrete type");
            }

            return concreteTypeRef;
        }
    }
}