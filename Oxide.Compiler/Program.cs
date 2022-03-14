using System;

namespace Oxide.Compiler;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.WriteLine("Usage: OxideCompiler <path>");
            return;
        }

        var driver = new OxideDriver();
        driver.Compile(args[0]);
    }
}