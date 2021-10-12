using Oxide.Compiler.Frontend;

namespace Oxide.Compiler
{
    class Program
    {
        static void Main(string[] args)
        {

            var frontend = new OxideFrontend();
            frontend.ParseFile("sample.ox");
        }
    }
}