using System.Collections.Generic;

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
    }
}