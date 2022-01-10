using System;
using System.Collections.Generic;
using Oxide.Compiler.Utils;

namespace Oxide.Compiler.Middleware.Lifetimes;

public class SlotState
{
    public int Slot { get; }

    public InstructionLifetime Instruction { get; }

    public SlotStatus Status { get; set; } = SlotStatus.Unprocessed;

    public HashSet<int> Values { get; private set; }

    public bool Borrowed { get; private set; }

    public int From { get; private set; }

    public string Field { get; private set; }

    public bool MutableBorrow { get; private set; }

    public string ErrorMessage { get; private set; }

    public SlotState(int slot, InstructionLifetime instruction)
    {
        Slot = slot;
        Instruction = instruction;
        Values = new HashSet<int>();
    }

    public bool Matches(SlotState otherState, bool ignoreValue)
    {
        return Status == otherState.Status && (ignoreValue || Equals(Values, otherState.Values)) &&
               Borrowed == otherState.Borrowed && From == otherState.From && Field == otherState.Field &&
               MutableBorrow == otherState.MutableBorrow;
    }

    public bool Propagate(SlotState previousSlot, bool skipChecks = false)
    {
        if (!skipChecks && (previousSlot.Status != SlotStatus.Active || previousSlot.Values.Count == 0))
        {
            throw new Exception("Cannot propagate non-active value");
        }

        if (Status == SlotStatus.Active || Status == SlotStatus.Moved)
        {
            // if ()
            // {
            //     
            // }

            if (Values.AddRange(previousSlot.Values))
            {
                Console.WriteLine("Propagate values");
                return true;
            }
            else
            {
                return false;
            }
        }
        else
        {
            Console.WriteLine($"Propagate new {Status} {string.Join(",", Values)}");
            
            Status = SlotStatus.Active;
            Values = new HashSet<int>(previousSlot.Values);
            Borrowed = previousSlot.Borrowed;
            From = previousSlot.From;
            Field = previousSlot.Field;
            MutableBorrow = previousSlot.MutableBorrow;

            return true;
        }
    }

    public bool NewValue(HashSet<int> values)
    {
        if (Status != SlotStatus.Unprocessed && Status != SlotStatus.NoValue && Status != SlotStatus.Active)
        {
            throw new Exception("Cannot overwrite value");
        }

        Status = SlotStatus.Active;
        // Values = new HashSet<int>(values);
        return Values.AddRange(values);
    }

    public void MarkBorrowed(int from, string field, bool mutable)
    {
        Borrowed = true;
        From = from;
        Field = field;
        MutableBorrow = mutable;
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
        Console.WriteLine("ERROR SET");
        var updated = Status != SlotStatus.Error;
        Status = SlotStatus.Error;
        ErrorMessage = (errId++) + msg;
        return updated;
    }

    public string ToDebugString()
    {
        var inner = string.Join(",", Values) + (Borrowed
            ? $"=&{(MutableBorrow ? "mut " : "")}${From}{(Field != null ? $" {Field}" : "")}"
            : "");

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