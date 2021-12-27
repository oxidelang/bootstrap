using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;

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
                case PointerTypeRef pointerTypeRef:
                    throw new NotImplementedException();
                case ReferenceTypeRef referenceTypeRef:
                    throw new NotImplementedException();
                default:
                    throw new ArgumentOutOfRangeException(nameof(a));
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

        public (Implementation imp, Function function) LookupImplementation(ConcreteTypeRef target,
            ConcreteTypeRef iface,
            string func)
        {
            foreach (var unit in _units)
            {
                var (i, f) = unit.LookupImplementation(this, target, iface, func);

                if (i != null)
                {
                    return (i, f);
                }
            }

            return (null, null);
        }
    }
}