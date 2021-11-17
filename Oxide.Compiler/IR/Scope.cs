using System;
using System.Collections.Generic;
using Oxide.Compiler.Frontend;

namespace Oxide.Compiler.IR
{
    public class Scope
    {
        public int Id { get; init; }

        public Scope ParentScope { get; init; }

        public Dictionary<int, VariableDeclaration> Variables { get; private set; }

        private readonly Dictionary<string, int> _variableMapping;
        // private readonly Dictionary<int, Block> _blocks;

        public Scope()
        {
            Variables = new Dictionary<int, VariableDeclaration>();
            _variableMapping = new Dictionary<string, int>();
            // _blocks = new Dictionary<int, Block>();
        }

        public VariableDeclaration DefineVariable(VariableDeclaration dec)
        {
            if (dec.ParameterSource.HasValue && ParentScope != null)
            {
                throw new Exception("Parameter variables can only be defined in root scope");
            }

            Variables.Add(dec.Id, dec);
            if (dec.Name != null)
            {
                _variableMapping[dec.Name] = dec.Id;
            }

            return dec;
        }

        public VariableDeclaration ResolveVariable(string name)
        {
            if (_variableMapping.TryGetValue(name, out var decId))
            {
                return Variables[decId];
            }

            return ParentScope?.ResolveVariable(name);
        }
    }
}