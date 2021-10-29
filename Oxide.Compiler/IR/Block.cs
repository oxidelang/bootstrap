using System.Collections.Generic;
using Oxide.Compiler.IR.Instructions;

namespace Oxide.Compiler.IR
{
    public class Block
    {
        public int Id { get; init; }

        public bool HasInstructions => Instructions.Count > 0;

        public List<Instruction> Instructions { get; private set; }

        private readonly HashSet<int> _incomingBlocks;

        private readonly HashSet<int> _outgoingBlocks;

        public Scope Scope { get; init; }

        public Block()
        {
            Instructions = new List<Instruction>();
            _incomingBlocks = new HashSet<int>();
            _outgoingBlocks = new HashSet<int>();
        }

        public Instruction AddInstruction(Instruction instruction)
        {
            Instructions.Add(instruction);
            return instruction;
        }
    }
}