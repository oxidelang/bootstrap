using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.IR
{
    public class IrStore
    {
        private readonly List<IrUnit> _units;

        public IrStore()
        {
            _units = new List<IrUnit>();
        }

        public (bool castable, bool @unsafe) CanCastTypes(TypeRef a, TypeRef b)
        {
            if (Equals(a, b))
            {
                return (true, false);
            }

            switch (a)
            {
                case BaseTypeRef baseTypeRef:
                    if (PrimitiveType.IsPrimitiveInt(a) && PrimitiveType.IsPrimitiveInt(b))
                    {
                        return (true, false);
                    }

                    return (Equals(baseTypeRef, b), false);
                case BorrowTypeRef:
                {
                    switch (b)
                    {
                        case PointerTypeRef:
                        case ReferenceTypeRef:
                        case BaseTypeRef:
                            return (false, false);
                        case BorrowTypeRef:
                            return (true, true);
                        default:
                            throw new ArgumentOutOfRangeException(nameof(b));
                    }
                }
                case PointerTypeRef:
                    return (true, b is not PointerTypeRef);
                case ReferenceTypeRef referenceTypeRef:
                    switch (b)
                    {
                        case BaseTypeRef:
                        case ReferenceTypeRef:
                            return (false, false);
                        case BorrowTypeRef otherBorrow:
                            if (!Equals(referenceTypeRef.InnerType, otherBorrow.InnerType))
                            {
                                return (false, false);
                            }

                            return (true, false);
                        case PointerTypeRef otherPointer:
                            if (!Equals(referenceTypeRef.InnerType, otherPointer.InnerType))
                            {
                                return (false, false);
                            }

                            return (true, true);
                        default:
                            throw new ArgumentOutOfRangeException(nameof(b));
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(a));
            }
        }

        public CopyProperties GetCopyProperties(TypeRef type)
        {
            switch (type)
            {
                case BorrowTypeRef:
                case PointerTypeRef:
                    return new CopyProperties
                    {
                        CanCopy = true,
                        BitwiseCopy = true
                    };
                case ReferenceTypeRef referenceTypeRef:
                    return new CopyProperties
                    {
                        CanCopy = true,
                        BitwiseCopy = false,
                        CopyMethod = new FunctionRef
                        {
                            TargetMethod = ConcreteTypeRef.From(
                                QualifiedName.From(
                                    "std",
                                    referenceTypeRef.StrongRef ? "box_copy_strong" : "box_copy_weak"
                                ),
                                referenceTypeRef.InnerType
                            )
                        }
                    };
                case ConcreteTypeRef concreteTypeRef:
                {
                    var baseType = Lookup(concreteTypeRef.Name);
                    switch (baseType)
                    {
                        case PrimitiveType primitiveType:
                            return new CopyProperties
                            {
                                CanCopy = true,
                                BitwiseCopy = true
                            };
                        case Struct @struct:
                            // TODO
                            return new CopyProperties
                            {
                                CanCopy = false
                            };
                        case Interface @interface:
                        case Variant variant:
                            throw new NotImplementedException();
                        default:
                            throw new ArgumentOutOfRangeException(nameof(baseType));
                    }
                }
                case BaseTypeRef baseTypeRef:
                    throw new Exception("Unresolved");
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }


        public void AddUnit(IrUnit unit)
        {
            _units.Add(unit);
        }

        public OxObj Lookup(QualifiedName qn, bool returnVariant = false)
        {
            return _units.Select(unit => unit.Lookup(qn, returnVariant)).FirstOrDefault(result => result != null);
        }

        public T Lookup<T>(QualifiedName qn) where T : OxObj
        {
            return _units.Select(unit => unit.Lookup<T>(qn)).FirstOrDefault(result => result != null);
        }

        public bool AreCompatible(Implementation imp, ImmutableArray<TypeRef> targetParams,
            out Dictionary<string, TypeRef> mappings)
        {
            if (imp.Target.GenericParams.Length != targetParams.Length)
            {
                throw new Exception("Generic parameter count mismatch");
            }

            mappings = new Dictionary<string, TypeRef>();

            for (var i = 0; i < imp.Target.GenericParams.Length; i++)
            {
                var impParam = imp.Target.GenericParams[i];
                var tgtParam = targetParams[i];

                if (impParam is not BaseTypeRef impBase || impBase is ConcreteTypeRef)
                {
                    impBase = impParam.GetBaseType();
                    if (impBase is not ConcreteTypeRef)
                    {
                        throw new Exception("Unexpected complex type");
                    }

                    var matchBase = tgtParam.GetBaseType();
                    if (matchBase is not ConcreteTypeRef)
                    {
                        throw new NotImplementedException("Generic constraint resolving");
                    }

                    if (!Equals(impParam, tgtParam))
                    {
                        return false;
                    }

                    continue;
                }

                if (impBase is not GenericTypeRef impGeneric)
                {
                    throw new Exception("Unexpected type");
                }

                if (!imp.GenericParams.Contains(impGeneric.Name))
                {
                    throw new Exception($"Unknown generic param {impGeneric.Name}");
                }

                // TODO: Resolve 'where' conditions

                mappings.Add(impGeneric.Name, tgtParam);
            }

            return true;
        }

        public ResolvedFunction ResolveFunction(ConcreteTypeRef target,
            string functionName)
        {
            return _units.Select(unit => unit.ResolveFunction(this, target, functionName))
                .FirstOrDefault(result => result != null);
        }

        public ResolvedFunction LookupImplementation(ConcreteTypeRef target, ConcreteTypeRef iface, string func)
        {
            return _units.Select(unit => unit.LookupImplementation(this, target, iface, func))
                .FirstOrDefault(result => result != null);
        }
    }
}