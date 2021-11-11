using System;
using System.IO;
using Oxide.Compiler.Backend.Llvm;
using Oxide.Compiler.Frontend;
using Oxide.Compiler.IR;

namespace Oxide.Compiler
{
    public class OxideDriver
    {
        private readonly IrStore _store;

        public OxideDriver()
        {
            _store = new IrStore();
        }

        public void Compile(string path)
        {
            Console.WriteLine($"Compiling {path}");
            var frontend = new OxideFrontend(_store);

            Console.WriteLine($"Parsing files");
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                Console.WriteLine($" - Parsing {file}");
                frontend.ParseFile(file);
            }

            Console.WriteLine("Processing");
            var unit = frontend.Process();

            Console.WriteLine("Dumping IR");
            var writer = new IrWriter();
            unit.WriteIr(writer);
            Console.WriteLine(writer.Generate());

            Console.WriteLine("Compiling");
            var backend = new LlvmBackend();
            backend.Begin();
            backend.CompileUnit(unit);
            backend.Complete();
            backend.Run();
        }
    }
}