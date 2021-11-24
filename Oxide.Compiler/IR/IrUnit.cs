using System;
using System.Collections.Generic;

namespace Oxide.Compiler.IR
{
    public class IrUnit
    {
        public Dictionary<QualifiedName, BaseDef> Objects { get; }
        public Dictionary<QualifiedName, StructDef> Structs { get; }
        public Dictionary<QualifiedName, VariantDef> Variants { get; }
        public Dictionary<QualifiedName, InterfaceDef> Interfaces { get; }
        public Dictionary<QualifiedName, FunctionDef> Functions { get; }

        public IrUnit()
        {
            Objects = new Dictionary<QualifiedName, BaseDef>();
            Structs = new Dictionary<QualifiedName, StructDef>();
            Variants = new Dictionary<QualifiedName, VariantDef>();
            Interfaces = new Dictionary<QualifiedName, InterfaceDef>();
            Functions = new Dictionary<QualifiedName, FunctionDef>();
        }

        public void WriteIr(IrWriter writer)
        {
            foreach (var functionDef in Functions.Values)
            {
                writer.WriteFunction(functionDef);
            }
        }

        public void AddStruct(StructDef def)
        {
            if (Objects.ContainsKey(def.Name))
            {
                throw new Exception($"Qualified name already exists {def.Name}");
            }

            Objects.Add(def.Name, def);
            Structs.Add(def.Name, def);
        }

        public void AddInterface(InterfaceDef def)
        {
            if (Objects.ContainsKey(def.Name))
            {
                throw new Exception($"Qualified name already exists {def.Name}");
            }

            Objects.Add(def.Name, def);
            Interfaces.Add(def.Name, def);
        }

        public void AddVariant(VariantDef def)
        {
            if (Objects.ContainsKey(def.Name))
            {
                throw new Exception($"Qualified name already exists {def.Name}");
            }

            Objects.Add(def.Name, def);
            Variants.Add(def.Name, def);
        }

        public void AddFunction(FunctionDef def)
        {
            if (Objects.ContainsKey(def.Name))
            {
                throw new Exception($"Qualified name already exists {def.Name}");
            }

            Objects.Add(def.Name, def);
            Functions.Add(def.Name, def);
        }

        public BaseDef LookupObject(QualifiedName qn)
        {
            return Objects.ContainsKey(qn) ? Objects[qn] : null;
        }
    }
}