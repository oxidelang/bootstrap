using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware;

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
            if (!Implementations.TryGetValue(implementation.Target.Name, out var ifaces))
            {
                ifaces = new List<Implementation>();
                Implementations.Add(implementation.Target.Name, ifaces);
            }

            if (!ifaces.Contains(implementation))
            {
                ifaces.Add(implementation);
            }
        }

        public ResolvedFunction ResolveFunction(IrStore store, ConcreteTypeRef target,
            string functionName)
        {
            if (!Implementations.TryGetValue(target.Name, out var imps))
            {
                return null;
            }

            var solutions = new List<ResolvedFunction>();
            foreach (var imp in imps)
            {
                if (target.GenericParams.Length != imp.Target.GenericParams.Length)
                {
                    throw new Exception("Generic length mismatch");
                }

                if (!store.AreCompatible(imp, target.GenericParams, out var knownGenerics))
                {
                    continue;
                }

                // TODO: Replace with more advance resolution system
                var impContext = new GenericContext(null, knownGenerics.ToImmutableDictionary(), null);

                var functions = imp.Functions;
                ConcreteTypeRef ifaceRef = null;
                if (imp.Interface != null)
                {
                    ifaceRef = (ConcreteTypeRef)impContext.ResolveRef(imp.Interface);
                    var iface = store.Lookup<Interface>(imp.Interface.Name);
                    functions = iface.Functions;
                }

                foreach (var function in functions)
                {
                    if (function.Name.Parts.Single() != functionName)
                    {
                        continue;
                    }

                    if (function.GenericParams != null && function.GenericParams.Count > 0)
                    {
                        throw new NotImplementedException("Generics");
                    }

                    solutions.Add(new ResolvedFunction
                    {
                        Interface = ifaceRef,
                        InterfaceGenerics = impContext.Generics,
                        Function = function
                    });
                    break;
                }
            }

            if (solutions.Count > 1)
            {
                throw new Exception("Conflicting implementations");
            }

            if (solutions.Count == 1)
            {
                return solutions.Single();
            }

            return null;
        }

        public (Implementation imp, Function function) LookupImplementation(IrStore store, ConcreteTypeRef target,
            ConcreteTypeRef iface, string func)
        {
            if (!Implementations.TryGetValue(target.Name, out var imps))
            {
                return (null, null);
            }

            foreach (var imp in imps)
            {
                if (target.GenericParams.Length != imp.Target.GenericParams.Length)
                {
                    throw new Exception("Generic length mismatch");
                }

                if (!store.AreCompatible(imp, target.GenericParams, out var knownGenerics))
                {
                    continue;
                }

                if (iface != null && iface.GenericParams != null && iface.GenericParams.Length > 0)
                {
                    throw new NotImplementedException("Generics");
                }

                if (!Equals(imp.Interface, iface))
                {
                    continue;
                }

                foreach (var function in imp.Functions)
                {
                    if (function.Name.Parts.Single() != func)
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
    }
}