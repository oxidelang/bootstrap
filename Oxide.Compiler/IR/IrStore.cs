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
        
    }
}