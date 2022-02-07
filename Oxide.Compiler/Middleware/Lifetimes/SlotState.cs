using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Compiler.Utils;

namespace Oxide.Compiler.Middleware.Lifetimes;

public class SlotState
{
    public int Slot { get; }

    public InstructionLifetime Instruction { get; }

    public SlotStatus Status { get; set; } = SlotStatus.Unprocessed;

    public HashSet<int> Values { get; private set; }

    public string ErrorMessage { get; private set; }

    public SlotState(int slot, InstructionLifetime instruction)
    {
        Slot = slot;
        Instruction = instruction;
        Values = new HashSet<int>();
    }

    public bool Matches(SlotState otherState, bool ignoreValue)
    {
        return Status == otherState.Status && (ignoreValue || Equals(Values, otherState.Values));
    }

    public bool Propagate(SlotState previousSlot, bool skipChecks = false)
    {
        if (!skipChecks && (previousSlot.Status != SlotStatus.Active || previousSlot.Values.Count == 0))
        {
            throw new Exception("Cannot propagate non-active value");
        }

        if (Status == SlotStatus.Unprocessed || Status == SlotStatus.NoValue)
        {
            Status = SlotStatus.Active;
        }

        return Values.AddRange(previousSlot.Values);
    }

    public bool AddValues(HashSet<int> values)
    {
        if (Status != SlotStatus.Unprocessed && Status != SlotStatus.NoValue && Status != SlotStatus.Active)
        {
            throw new Exception("Cannot overwrite value");
        }

        Status = SlotStatus.Active;
        return Values.AddRange(values);
    }

    public void NoValue()
    {
        if (Status != SlotStatus.Unprocessed && Status != SlotStatus.NoValue)
        {
            throw new Exception("Cannot overwrite value");
        }

        Status = SlotStatus.NoValue;
    }

    public void Move()
    {
        if (Status != SlotStatus.Active)
        {
            throw new Exception("Cannot mark non-active as moved");
        }

        Status = SlotStatus.Moved;
    }

    private static int errId = 1;

    public bool Error(string msg)
    {
        var updated = Status != SlotStatus.Error;
        Status = SlotStatus.Error;
        ErrorMessage = (errId++) + msg;
        return updated;
    }

    public string ToDebugString()
    {
        var reqs = Instruction.FunctionLifetime.ValueRequirements;

        var inner = string.Join(",",
            Values.Select(x => { return x + ":(" + string.Join(",", reqs[x].Select(r => r.Value.ToString())) + ")"; }));


        switch (Status)
        {
            case SlotStatus.Unprocessed:
                return "???";
            case SlotStatus.NoValue:
                return "";
            case SlotStatus.Active:
                return $"Active({inner})";
            case SlotStatus.Moved:
                return $"Moved({inner})";
            case SlotStatus.Error:
                return $"Error({ErrorMessage})";
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}