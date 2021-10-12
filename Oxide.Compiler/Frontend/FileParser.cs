using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR;
using Oxide.Compiler.Parser;

namespace Oxide.Compiler.Frontend
{
    public class FileParser
    {
        private QualifiedName _package;
        private List<Import> _imports;

        public void Parse(OxideParser.Compilation_unitContext cu)
        {
            _package = cu.package().qualified_name().Parse();
            Console.WriteLine($"Package: {_package}");

            _imports = new List<Import>();
            foreach (var importStmt in cu.import_stmt())
            {
                _imports.Add(new Import(
                    importStmt.qualified_name().Parse(),
                    importStmt.name()?.GetText()
                ));
            }

            var structDefs = new List<OxideParser.Struct_defContext>();
            var fnDefs = new List<OxideParser.Fn_defContext>();
            var implBlocks = new List<OxideParser.Impl_stmtContext>();
            foreach (var tl in cu.top_level())
            {
                switch (tl)
                {
                    case OxideParser.Struct_top_levelContext structTopLevel:
                        structDefs.Add(structTopLevel.struct_def());
                        break;
                    case OxideParser.Fn_top_levelContext fnTopLevel:
                        fnDefs.Add(fnTopLevel.fn_def());
                        break;
                    case OxideParser.Impl_top_levelContext implTopLevel:
                        implBlocks.Add(implTopLevel.impl_stmt());
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(tl));
                }
            }

            foreach (var ctx in structDefs)
            {
                ParseStruct(ctx);
            }
        }

        private void ParseStruct(OxideParser.Struct_defContext ctx)
        {
            var vis = ctx.visibility().Parse();
            var name = ctx.name().GetText();
            var genericParams = ctx.generic_def()?.Parse() ?? new List<string>();

            foreach (var fieldDef in ctx.field_def())
            {
            }
        }

        private TypeDef ParseType(OxideParser.TypeContext ctx, List<string> genericTypes)
        {
            var flags = ctx.type_flags();

            TypeCategory category;
            if (flags.REF() != null)
            {
                category = TypeCategory.StrongReference;
            }
            else if (flags.DERIVED() != null)
            {
                throw new NotImplementedException("DERIVED not implemented");
            }
            else if (flags.WEAK() != null)
            {
                category = TypeCategory.WeakReference;
            }
            else
            {
                category = TypeCategory.Direct;
            }

            var genericParams = new List<TypeDef>();
            if (ctx.type_generic_params() != null)
            {
                genericParams.AddRange(ctx.type_generic_params().type().Select(x => ParseType(x, genericTypes)));
            }

            var rawQn = ctx.qualified_name().Parse();

            TypeSource source;
            QualifiedName qn;
            if (!rawQn.IsAbsolute && genericTypes.Contains(rawQn.Parts[0]))
            {
                source = TypeSource.Generic;
                qn = rawQn;
            }
            else
            {
                source = TypeSource.Concrete;
                qn = ResolveQN(rawQn);
            }

            return new TypeDef
            {
                Category = category,
                Mutable = flags.MUT() != null,
                GenericParams = genericParams.ToImmutableList(),
                Source = source,
                Name = qn
            };
        }

        private QualifiedName ResolveQN(QualifiedName qn)
        {
            if (qn.IsAbsolute)
            {
                return qn;
            }

            foreach (var import in _imports)
            {
                if (import.Target == qn.Parts[0])
                {
                    return new QualifiedName(true, import.Source.Parts.AddRange(qn.Parts.Skip(1)));
                }
            }

            // Assume current package
            return new QualifiedName(true, _package.Parts.AddRange(qn.Parts));
        }
    }
}