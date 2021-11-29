using System;
using System.Collections.Generic;
using Oxide.Compiler.IR.Instructions;

namespace Oxide.Compiler.IR.Types
{
    public class Block
    {
        public int Id { get; init; }

        public bool HasInstructions => Instructions.Count > 0;

        public List<Instruction> Instructions { get; private set; }

        private readonly HashSet<int> _incomingBlocks;

        private readonly HashSet<int> _outgoingBlocks;

        public Scope Scope { get; init; }

        public bool HasTerminated;

        public Block()
        {
            Instructions = new List<Instruction>();
            _incomingBlocks = new HashSet<int>();
            _outgoingBlocks = new HashSet<int>();
            HasTerminated = false;
        }

        public Instruction AddInstruction(Instruction instruction)
        {
            if (HasTerminated)
            {
                throw new Exception("Block has terminated");
            }

            if (instruction.Terminal)
            {
                HasTerminated = true;
            }
            
            Instructions.Add(instruction);
            return instruction;
        }
    }
}