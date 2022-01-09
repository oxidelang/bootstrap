using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Antlr4.Runtime;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware.Usage;
using Oxide.Compiler.Parser;

namespace Oxide.Compiler.Frontend;

public class OxideFrontend
{
    private readonly IrStore _store;
    private IrUnit _unit;

    private readonly List<FileParser> _parsers;

    public OxideFrontend(IrStore store)
    {
        _store = store;
        _parsers = new List<FileParser>();
    }

    public void ParseFile(string file)
    {
        var lexer = new OxideLexer(new AntlrFileStream(file));
        var parser = new OxideParser(new CommonTokenStream(lexer));

        var fp = new FileParser();
        fp.Parse(parser.compilation_unit());

        _parsers.Add(fp);
    }

    public IrUnit Process()
    {
        _unit = new IrUnit();

        foreach (var fp in _parsers)
        {
            foreach (var def in fp.Structs.Values)
            {
                _unit.Add(def);
            }

            foreach (var def in fp.Interfaces.Values)
            {
                _unit.Add(def);
            }

            foreach (var def in fp.Variants.Values)
            {
                _unit.Add(def);
            }

            foreach (var def in fp.Functions.Values)
            {
                _unit.Add(def);
            }

            foreach (var imps in fp.Implementations.Values)
            {
                foreach (var imp in imps)
                {
                    _unit.AddImplementation(imp);
                }
            }
        }

        foreach (var fp in _parsers)
        {
            foreach (var functionDef in fp.Functions.Values)
            {
                if (!functionDef.HasBody)
                {
                    continue;
                }

                var unparsedBody = fp.UnparsedBodies[functionDef];
                var bodyParser = new BodyParser(_store, _unit, fp, functionDef, null, ImmutableArray<string>.Empty,
                    WhereConstraints.Default);
                functionDef.EntryBlock = bodyParser.ParseBody(unparsedBody);
                functionDef.Blocks = bodyParser.Blocks.Values.ToImmutableList();
                functionDef.Scopes = bodyParser.Scopes.ToImmutableList();
            }

            foreach (var iface in fp.Interfaces.Values)
            {
                foreach (var functionDef in iface.Functions)
                {
                    if (!functionDef.HasBody)
                    {
                        continue;
                    }

                    var unparsedBody = fp.UnparsedBodies[functionDef];
                    var bodyParser = new BodyParser(_store, _unit, fp, functionDef, null,
                        ImmutableArray<string>.Empty, WhereConstraints.Default);
                    functionDef.EntryBlock = bodyParser.ParseBody(unparsedBody);
                    functionDef.Blocks = bodyParser.Blocks.Values.ToImmutableList();
                    functionDef.Scopes = bodyParser.Scopes.ToImmutableList();
                }
            }

            foreach (var imps in fp.Implementations.Values)
            {
                foreach (var imp in imps)
                {
                    foreach (var functionDef in imp.Functions)
                    {
                        if (!functionDef.HasBody)
                        {
                            continue;
                        }

                        var unparsedBody = fp.UnparsedBodies[functionDef];
                        var bodyParser = new BodyParser(_store, _unit, fp, functionDef, imp.Target,
                            imp.GenericParams, imp.Constraints);
                        functionDef.EntryBlock = bodyParser.ParseBody(unparsedBody);
                        functionDef.Blocks = bodyParser.Blocks.Values.ToImmutableList();
                        functionDef.Scopes = bodyParser.Scopes.ToImmutableList();
                    }
                }
            }
        }

        return _unit;
    }
}