namespace Oxide.Compiler
{
    class Program
    {
        static void Main(string[] args)
        {
            var driver = new OxideDriver();
            driver.Compile(args[0]);
        }
    }
}