using System;

namespace Oxide.Compiler.Middleware.Lifetimes;

public class SlotState
{
    // public int Slot { get; set; }

    public SlotStatus Status { get; set; } = SlotStatus.Unprocessed;

    public int Value { get; private set; }

    public bool Borrowed { get; private set; }

    public int From { get; private set; }

    public string Field { get; private set; }

    public bool MutableBorrow { get; private set; }

    public bool Matches(SlotState otherState, bool ignoreValue)
    {
        return Status == otherState.Status && (Value == otherState.Value || ignoreValue) &&
               Borrowed == otherState.Borrowed && From == otherState.From && Field == otherState.Field &&
               MutableBorrow == otherState.MutableBorrow;
    }

    public void Propagate(SlotState previousSlot, bool skipChecks = false)
    {
        if (!skipChecks && (previousSlot.Status != SlotStatus.Active || Value != 0))
        {
            throw new Exception("Cannot propagate non-active value");
        }

        Status = SlotStatus.Active;
        Value = previousSlot.Value;
        Borrowed = previousSlot.Borrowed;
        From = previousSlot.From;
        Field = previousSlot.Field;
        MutableBorrow = previousSlot.MutableBorrow;
    }

    public void NewValue(int value)
    {
        if (Status != SlotStatus.Unprocessed && Status != SlotStatus.NoValue)
        {
            throw new Exception("Cannot overwrite value");
        }

        Status = SlotStatus.Active;
        Value = value;
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

    public void Error()
    {
        Status = SlotStatus.Error;
    }

    public string ToDebugString()
    {
        var inner = Borrowed
            ? $"{Value}=&{(MutableBorrow ? "mut " : "")}${From}{(Field != null ? $" {Field}" : "")}"
            : $"{Value}";

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
                return "Error";
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}