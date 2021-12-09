using System;
using System.IO;
using Oxide.Compiler.Backend.Llvm;
using Oxide.Compiler.Frontend;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware;

namespace Oxide.Compiler
{
    public class OxideDriver
    {
        private readonly IrStore _store;

        public OxideDriver()
        {
            _store = new IrStore();

            var inbuilt = new IrUnit();
            inbuilt.Add(PrimitiveType.I32);
            inbuilt.Add(PrimitiveType.Bool);
            _store.AddUnit(inbuilt);
        }

        public void Compile(string path)
        {
            Console.WriteLine($"Compiling {path}");
            var frontend = new OxideFrontend(_store);

            Console.WriteLine($"Parsing files");
            foreach (var file in Directory.GetFiles(path, "*.ox", SearchOption.AllDirectories))
            {
                Console.WriteLine($" - Parsing {file}");
                frontend.ParseFile(file);
            }

            Console.WriteLine("Processing");
            var unit = frontend.Process();
            _store.AddUnit(unit);

            Console.WriteLine("Dumping IR");
            var writer = new IrWriter();
            unit.WriteIr(writer);
            var ir = writer.Generate();
            File.WriteAllText($"{path}/compiled.preopt.ir", ir);

            Console.WriteLine("Running middleware");
            var middleware = new MiddlewareManager(_store);
            middleware.Process(unit);

            Console.WriteLine("Dumping IR");
            writer = new IrWriter();
            unit.WriteIr(writer);
            ir = writer.Generate();
            File.WriteAllText($"{path}/compiled.opt.ir", ir);

            Console.WriteLine("Compiling");
            var backend = new LlvmBackend(_store, middleware);
            backend.Begin();
            backend.Compile();
            backend.Complete(path);
            backend.Run();
        }
    }
}