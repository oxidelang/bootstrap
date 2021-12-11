using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.IR
{
    public class IrUnit
    {
        public Dictionary<QualifiedName, OxObj> Objects { get; }

        public Dictionary<QualifiedName, List<Implementation>> Implementations { get; }

        public IrUnit()
        {
            Objects = new Dictionary<QualifiedName, OxObj>();
            Implementations = new Dictionary<QualifiedName, List<Implementation>>();
        }

        public void WriteIr(IrWriter writer)
        {
            var funcs = new List<Function>();
            var ifaces = new List<Interface>();

            foreach (var obj in Objects.Values)
            {
                switch (obj)
                {
                    case Function function:
                        funcs.Add(function);
                        break;
                    case Struct @struct:
                        writer.WriteStruct(@struct);
                        break;
                    case Interface iface:
                        ifaces.Add(iface);
                        break;
                    case Variant variant:
                        throw new NotImplementedException();
                    case PrimitiveType primitiveType:
                        throw new Exception("Unexpected");
                    default:
                        throw new ArgumentOutOfRangeException(nameof(obj));
                }
            }

            foreach (var iface in ifaces)
            {
                writer.WriteInterface(iface);
            }

            foreach (var imps in Implementations.Values)
            {
                foreach (var imp in imps)
                {
                    writer.WriteImplementation(imp);
                }
            }

            foreach (var func in funcs)
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

        public void AddImplementation(Implementation implementation)
        {
            if (!Implementations.TryGetValue(implementation.Target, out var ifaces))
            {
                ifaces = new List<Implementation>();
                Implementations.Add(implementation.Target, ifaces);
            }

            if (!ifaces.Contains(implementation))
            {
                ifaces.Add(implementation);
            }
        }

        public List<Implementation> LookupImplementations(QualifiedName target, QualifiedName iface)
        {
            return Implementations.TryGetValue(target, out var ifaces)
                ? ifaces.Where(x => Equals(x.Interface, iface)).ToList()
                : new List<Implementation>();
        }

        public (Implementation imp, Function function) LookupImplementationFunction(QualifiedName target,
            string functionName)
        {
            if (!Implementations.TryGetValue(target, out var imps))
            {
                return (null, null);
            }

            foreach (var imp in imps)
            {
                foreach (var function in imp.Functions)
                {
                    if (function.Name.Parts.Single() != functionName)
                    {
                        continue;
                    }

                    if (function.GenericParams != null && function.GenericParams.Count > 0)
                    {
                        throw new NotImplementedException("Generics");
                    }

                    return (imp, function);
                }
            }

            return (null, null);
        }

        public (Implementation imp, Function function) LookupImplementation(QualifiedName type, QualifiedName imp,
            string func)
        {
            if (!Implementations.TryGetValue(type, out var imps))
            {
                return (null, null);
            }

            foreach (var implementation in imps)
            {
                if (!Equals(implementation.Interface, imp))
                {
                    continue;
                }

                foreach (var function in implementation.Functions)
                {
                    if (function.Name.Parts.Single() != func)
                    {
                        continue;
                    }

                    if (function.GenericParams != null && function.GenericParams.Count > 0)
                    {
                        throw new NotImplementedException("Generics");
                    }

                    return (implementation, function);
                }
            }

            return (null, null);
        }
    }
}