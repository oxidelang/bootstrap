using System.Collections.Generic;
using Oxide.Compiler.Frontend;

namespace Oxide.Compiler.IR
{
    public class Scope
    {
        public int Id { get; init; }

        public Scope ParentScope { get; init; }

        private readonly Dictionary<int, VariableDeclaration> _variables;

        private readonly Dictionary<string, int> _variableMapping;
        // private readonly Dictionary<int, Block> _blocks;

        public Scope()
        {
            _variables = new Dictionary<int, VariableDeclaration>();
            _variableMapping = new Dictionary<string, int>();
            // _blocks = new Dictionary<int, Block>();
        }

        public VariableDeclaration DefineVariable(VariableDeclaration dec)
        {
            _variables.Add(dec.Id, dec);
            _variableMapping[dec.Name] = dec.Id;
            return dec;
        }
    }
}