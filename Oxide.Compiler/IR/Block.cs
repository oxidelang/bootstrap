using System.Collections.Generic;
using Oxide.Compiler.IR.Instructions;

namespace Oxide.Compiler.IR
{
    public class Block
    {
        public int Id { get; init; }

        public bool HasInstructions => _instructions.Count > 0;

        private readonly List<Instruction> _instructions;

        private readonly HashSet<int> _incomingBlocks;

        private readonly HashSet<int> _outgoingBlocks;

        public Scope Scope { get; init; }

        public Block()
        {
            _instructions = new List<Instruction>();
            _incomingBlocks = new HashSet<int>();
            _outgoingBlocks = new HashSet<int>();
        }

        public Instruction AddInstruction(Instruction instruction)
        {
            _instructions.Add(instruction);
            return instruction;
        }
    }
}