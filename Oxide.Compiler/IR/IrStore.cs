using System.Collections.Generic;
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

        public void AddUnit(IrUnit unit)
        {
            _units.Add(unit);
        }

        public IrUnit FindUnitForQn(QualifiedName qn)
        {
            foreach (var unit in _units)
            {
                if (unit.Objects.ContainsKey(qn))
                {
                    return unit;
                }
            }

            return null;
        }

        public OxObj Lookup(QualifiedName qn)
        {
            foreach (var unit in _units)
            {
                if (unit.Objects.ContainsKey(qn))
                {
                    return unit.Lookup(qn);
                }
            }

            return null;
        }

        public T Lookup<T>(QualifiedName qn) where T : OxObj
        {
            foreach (var unit in _units)
            {
                if (unit.Objects.ContainsKey(qn))
                {
                    return unit.Lookup<T>(qn);
                }
            }

            return null;
        }

        public (Implementation imp, Function function) LookupImplementationFunction(QualifiedName target,
            string functionName)
        {
            foreach (var unit in _units)
            {
                var (i, f) = unit.LookupImplementationFunction(target, functionName);

                if (i != null)
                {
                    return (i, f);
                }
            }

            return (null, null);
        }

        public (Implementation imp, Function function) LookupImplementation(QualifiedName type, QualifiedName imp,
            string func)
        {
            foreach (var unit in _units)
            {
                var (i, f) = unit.LookupImplementation(type, imp, func);

                if (i != null)
                {
                    return (i, f);
                }
            }

            return (null, null);
        }
    }
}