using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.IR
{
    public class IrUnit
    {
        public Dictionary<QualifiedName, OxObj> Objects { get; }

        public IrUnit()
        {
            Objects = new Dictionary<QualifiedName, OxObj>();
        }

        public void WriteIr(IrWriter writer)
        {
            foreach (var func in Objects.Values.Where(x => x is Function).Cast<Function>())
            {
                writer.WriteFunction(func);
            }
        }

        public void Add(OxObj def)
        {
            if (Objects.ContainsKey(def.Name))
            {
                throw new Exception($"Qualified name already exists {def.Name}");
            }

            Objects.Add(def.Name, def);
        }

        public OxObj Lookup(QualifiedName qn)
        {
            return Objects.ContainsKey(qn) ? Objects[qn] : null;
        }


        public T Lookup<T>(QualifiedName qn) where T : OxObj
        {
            if (!Objects.ContainsKey(qn))
            {
                return default;
            }

            var found = Objects[qn];
            if (found is not T obj)
            {
                throw new Exception($"{qn} is not of expected type");
            }

            return obj;
        }
    }
}