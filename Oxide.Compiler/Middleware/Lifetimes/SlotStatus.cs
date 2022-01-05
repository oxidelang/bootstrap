namespace Oxide.Compiler.Middleware.Lifetimes;

public enum SlotStatus
{
    Unprocessed,
    NoValue,
    Active,
    Moved,
    Error
}