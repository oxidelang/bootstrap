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
        private Dictionary<QualifiedName, StructDef> _structs;
        private Dictionary<QualifiedName, VariantDef> _variants;
        private Dictionary<QualifiedName, InterfaceDef> _interfaces;

        public void Parse(OxideParser.Compilation_unitContext cu)
        {
            _package = cu.package().qualified_name().Parse(true);
            Console.WriteLine($"Package: {_package}");

            _imports = new List<Import>();
            foreach (var importStmt in cu.import_stmt())
            {
                _imports.Add(new Import(
                    importStmt.qualified_name().Parse(true),
                    importStmt.name()?.GetText()
                ));
            }

            // Split file
            var structDefs = new List<OxideParser.Struct_defContext>();
            var variantsDefs = new List<OxideParser.Variant_defContext>();
            var funcsDefs = new List<OxideParser.Func_defContext>();
            var ifaceDefs = new List<OxideParser.Iface_defContext>();
            var implBlocks = new List<OxideParser.Impl_stmtContext>();
            var aliasDefs = new List<OxideParser.Alias_defContext>();
            foreach (var tl in cu.top_level())
            {
                switch (tl)
                {
                    case OxideParser.Alias_top_levelContext aliasTopLevel:
                        aliasDefs.Add(aliasTopLevel.alias_def());
                        break;
                    case OxideParser.Struct_top_levelContext structTopLevel:
                        structDefs.Add(structTopLevel.struct_def());
                        break;
                    case OxideParser.Variant_top_levelContext variantTopLevel:
                        variantsDefs.Add(variantTopLevel.variant_def());
                        break;
                    case OxideParser.Func_top_levelContext fnTopLevel:
                        funcsDefs.Add(fnTopLevel.func_def());
                        break;
                    case OxideParser.Iface_top_levelContext ifaceTopLevel:
                        ifaceDefs.Add(ifaceTopLevel.iface_def());
                        break;
                    case OxideParser.Impl_top_levelContext implTopLevel:
                        implBlocks.Add(implTopLevel.impl_stmt());
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(tl));
                }
            }

            // Parse structs
            _structs = new Dictionary<QualifiedName, StructDef>();
            foreach (var ctx in structDefs)
            {
                ParseStruct(ctx);
            }

            // Parse variant types
            _variants = new Dictionary<QualifiedName, VariantDef>();
            foreach (var ctx in variantsDefs)
            {
                ParseVariant(ctx);
            }

            // Parse interfaces
            _interfaces = new Dictionary<QualifiedName, InterfaceDef>();
            foreach (var ctx in ifaceDefs)
            {
                throw new NotImplementedException("Interfaces");
            }
        }

        private void ParseStruct(OxideParser.Struct_defContext ctx)
        {
            var vis = ctx.visibility().Parse();
            var structName = new QualifiedName(true, _package.Parts.Add(ctx.name().GetText()));
            var genericParams = ctx.generic_def()?.Parse() ?? new List<string>();
            var fields = new List<FieldDef>();

            foreach (var fieldDef in ctx.field_def())
            {
                var type = ParseType(fieldDef.type(), genericParams.ToImmutableList());
                fields.Add(new FieldDef
                {
                    Name = fieldDef.name().GetText(),
                    Visibility = fieldDef.visibility().Parse(),
                    Type = type,
                    Mutable = fieldDef.MUT() != null,
                });
            }

            _structs.Add(
                structName,
                new StructDef(structName, vis, genericParams.ToImmutableList(), fields.ToImmutableList())
            );
        }

        private void ParseVariant(OxideParser.Variant_defContext ctx)
        {
            var vis = ctx.visibility().Parse();
            var variantName = new QualifiedName(true, _package.Parts.Add(ctx.name().GetText()));
            var genericParams = ctx.generic_def()?.Parse() ?? new List<string>();
            var items = new Dictionary<string, VariantItemDef>();

            foreach (var itemDef in ctx.variant_item_def())
            {
                switch (itemDef)
                {
                    case OxideParser.Simple_variant_item_defContext simpleDef:
                    {
                        var itemName = simpleDef.name().GetText();
                        items.Add(itemName, new VariantItemDef
                        {
                            Name = itemName,
                            Content = null
                        });
                        break;
                    }
                    case OxideParser.Struct_variant_item_defContext structDef:
                    {
                        var itemName = structDef.name().GetText();
                        var itemFields = new List<FieldDef>();

                        foreach (var fieldDef in structDef.field_def())
                        {
                            var type = ParseType(fieldDef.type(), genericParams.ToImmutableList());
                            itemFields.Add(new FieldDef
                            {
                                Name = fieldDef.name().GetText(),
                                Visibility = fieldDef.visibility().Parse(Visibility.Public),
                                Type = type,
                                Mutable = fieldDef.MUT() != null,
                            });
                        }

                        items.Add(itemName, new VariantItemDef
                        {
                            Name = itemName,
                            Content = new StructDef(
                                new QualifiedName(true, variantName.Parts.Add(itemName)),
                                Visibility.Public,
                                genericParams.ToImmutableList(),
                                itemFields.ToImmutableList()
                            ),
                        });
                        break;
                    }
                    case OxideParser.Tuple_variant_item_defContext tupleDef:
                    {
                        var itemName = tupleDef.name().GetText();
                        var itemFields = new List<FieldDef>();

                        var itemId = 0;
                        foreach (var tupleCtx in tupleDef.tuple_def().tuple_item_def())
                        {
                            var type = ParseType(tupleCtx.type(), genericParams.ToImmutableList());
                            itemFields.Add(new FieldDef
                            {
                                Name = "item" + itemId++,
                                Mutable = tupleCtx.MUT() != null,
                                Type = type,
                                Visibility = Visibility.Public
                            });
                        }

                        items.Add(itemName, new VariantItemDef
                        {
                            Name = itemName,
                            Content = new StructDef(
                                new QualifiedName(true, variantName.Parts.Add(itemName)),
                                Visibility.Public,
                                genericParams.ToImmutableList(),
                                itemFields.ToImmutableList()
                            ),
                        });
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(itemDef));
                }
            }

            _variants.Add(
                variantName,
                new VariantDef
                {
                    Name = variantName,
                    Visibility = vis,
                    GenericParams = genericParams.ToImmutableList(),
                    Items = items.ToImmutableDictionary()
                }
            );
        }

        private void ParseInterface(OxideParser.Iface_defContext ctx)
        {
            var vis = ctx.visibility().Parse();
            var structName = new QualifiedName(true, _package.Parts.Add(ctx.name().GetText()));
            var genericParams = ctx.generic_def()?.Parse() ?? new List<string>();
        }

        private TypeDef ParseType(OxideParser.TypeContext ctx, ImmutableList<string> genericTypes)
        {
            TypeCategory category;
            var mutable = false;
            switch (ctx.type_flags())
            {
                case OxideParser.Direct_type_flagsContext direct:
                    category = TypeCategory.Direct;
                    break;
                case OxideParser.Local_type_flagsContext local:
                    category = TypeCategory.Reference;
                    mutable = local.MUT() != null;
                    break;
                case OxideParser.Ptr_type_flagsContext ptr:
                    category = TypeCategory.Pointer;
                    mutable = ptr.MUT() != null;
                    break;
                case OxideParser.Ref_type_flagsContext refType:
                    if (refType.REF() != null)
                    {
                        category = TypeCategory.StrongReference;
                    }
                    else if (refType.DERIVED() != null)
                    {
                        throw new NotImplementedException("DERIVED not implemented");
                    }
                    else if (refType.WEAK() != null)
                    {
                        category = TypeCategory.WeakReference;
                    }
                    else
                    {
                        throw new Exception("Unknown ref type");
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException();
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
                Mutable = mutable,
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

            foreach (var import in _imports.AsEnumerable().Reverse())
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