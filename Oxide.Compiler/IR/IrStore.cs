using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.IR;

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

                if (baseTypeRef is ConcreteTypeRef concreteTypeRef && b is DerivedRefTypeRef derivedRefTypeRef)
                {
                    if (Equals(concreteTypeRef.Name, QualifiedName.From("std", "DerivedBox")) &&
                        derivedRefTypeRef.StrongRef && Equals(derivedRefTypeRef.InnerType,
                            concreteTypeRef.GenericParams.Single()))
                    {
                        return (true, false);
                    }
                }

                return (Equals(baseTypeRef, b), false);
            case BorrowTypeRef borrowTypeRef:
            {
                switch (b)
                {
                    case ReferenceTypeRef:
                    case BaseTypeRef:
                        return (false, false);
                    case BorrowTypeRef:
                        return (true, true);
                    case PointerTypeRef otherPointer:
                        if (!Equals(borrowTypeRef.InnerType, otherPointer.InnerType))
                        {
                            return (false, false);
                        }

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
                        return (false, false);
                    case ReferenceTypeRef otherReference:
                        if (!Equals(referenceTypeRef.InnerType, otherReference.InnerType))
                        {
                            return (false, false);
                        }

                        return (referenceTypeRef.StrongRef && !otherReference.StrongRef, false);
                    case BorrowTypeRef:
                        return (false, false);
                    case PointerTypeRef otherPointer:
                        if (
                            !Equals(referenceTypeRef.InnerType, otherPointer.InnerType) && !Equals(
                                otherPointer.InnerType,
                                ConcreteTypeRef.From(QualifiedName.From("std", "Box"), referenceTypeRef.InnerType))
                        )
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

    public CopyProperties GetCopyProperties(TypeRef type, WhereConstraints constraints = null)
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
            case DerivedRefTypeRef derivedRefTypeRef:
                return new CopyProperties
                {
                    CanCopy = true,
                    BitwiseCopy = false,
                    CopyMethod = new FunctionRef
                    {
                        TargetMethod = ConcreteTypeRef.From(
                            QualifiedName.From(
                                "std",
                                derivedRefTypeRef.StrongRef ? "derived_copy_strong" : "derived_copy_weak"
                            ),
                            derivedRefTypeRef.InnerType
                        )
                    }
                };
            case ConcreteTypeRef concreteTypeRef:
            {
                var baseType = Lookup(concreteTypeRef.Name);
                switch (baseType)
                {
                    case PrimitiveType:
                        return new CopyProperties
                        {
                            CanCopy = true,
                            BitwiseCopy = true
                        };
                    case Variant:
                    case Struct:
                    {
                        var resolvedFunction = LookupImplementation(concreteTypeRef, CopyableType, "copy");
                        if (resolvedFunction != null)
                        {
                            return new CopyProperties
                            {
                                CanCopy = true,
                                BitwiseCopy = false,
                                CopyMethod = new FunctionRef
                                {
                                    TargetType = concreteTypeRef,
                                    TargetImplementation = CopyableType,
                                    TargetMethod = ConcreteTypeRef.From(QualifiedName.FromRelative("copy"))
                                }
                            };
                        }

                        return new CopyProperties
                        {
                            CanCopy = false
                        };
                    }
                    case Interface @interface:
                        throw new NotImplementedException();

                    default:
                        throw new ArgumentOutOfRangeException(nameof(baseType));
                }
            }
            case GenericTypeRef genericTypeRef:
            {
                if (constraints == null)
                {
                    throw new Exception("Unresolved");
                }

                var paramConstraints = constraints.GetConstraints(genericTypeRef.Name);

                if (paramConstraints.Any(x => Equals(x, CopyableType)))
                {
                    return new CopyProperties
                    {
                        CanCopy = true,
                        BitwiseCopy = false,
                        CopyMethod = new FunctionRef
                        {
                            TargetImplementation = CopyableType,
                            TargetMethod = ConcreteTypeRef.From(QualifiedName.FromRelative("copy"))
                        }
                    };
                }

                return new CopyProperties
                {
                    CanCopy = false
                };
            }
            case BaseTypeRef baseTypeRef:
                throw new Exception("Unresolved");
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    public static ConcreteTypeRef CopyableType = ConcreteTypeRef.From(QualifiedName.From("std", "Copyable"));

    public FunctionRef GetDropFunction(ConcreteTypeRef concreteTypeRef)
    {
        var baseType = Lookup(concreteTypeRef.Name);
        switch (baseType)
        {
            case PrimitiveType:
                return null;
            case Variant:
            case Struct:
            {
                var resolvedFunction = LookupImplementation(concreteTypeRef, DropType, "drop");
                if (resolvedFunction != null)
                {
                    return new FunctionRef
                    {
                        TargetType = concreteTypeRef,
                        TargetImplementation = DropType,
                        TargetMethod = ConcreteTypeRef.From(QualifiedName.FromRelative("drop"))
                    };
                }

                return null;
            }
            case Interface @interface:
                throw new NotImplementedException();

            default:
                throw new ArgumentOutOfRangeException(nameof(baseType));
        }
    }

    public static ConcreteTypeRef DropType = ConcreteTypeRef.From(QualifiedName.From("std", "Drop"));

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

    public static bool AreCompatible(Implementation imp, ImmutableArray<TypeRef> targetParams,
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