using System;
using System.Collections.Generic;

namespace Oxide.Compiler.IR
{
    public class IrUnit
    {
        public HashSet<QualifiedName> Objects { get; }
        public Dictionary<QualifiedName, StructDef> Structs { get; }
        public Dictionary<QualifiedName, VariantDef> Variants { get; }
        public Dictionary<QualifiedName, InterfaceDef> Interfaces { get; }
        public Dictionary<QualifiedName, FunctionDef> Functions { get; }

        public IrUnit()
        {
            Objects = new HashSet<QualifiedName>();
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
            if (!Objects.Add(def.Name))
            {
                throw new Exception($"Qualified name already exists {def.Name}");
            }

            Structs.Add(def.Name, def);
        }

        public void AddInterface(InterfaceDef def)
        {
            if (!Objects.Add(def.Name))
            {
                throw new Exception($"Qualified name already exists {def.Name}");
            }

            Interfaces.Add(def.Name, def);
        }

        public void AddVariant(VariantDef def)
        {
            if (!Objects.Add(def.Name))
            {
                throw new Exception($"Qualified name already exists {def.Name}");
            }

            Variants.Add(def.Name, def);
        }

        public void AddFunction(FunctionDef def)
        {
            if (!Objects.Add(def.Name))
            {
                throw new Exception($"Qualified name already exists {def.Name}");
            }

            Functions.Add(def.Name, def);
        }
    }
}