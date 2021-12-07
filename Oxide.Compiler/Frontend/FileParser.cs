using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Parser;

namespace Oxide.Compiler.Frontend
{
    public class FileParser
    {
        public QualifiedName Package { get; private set; }
        public List<Import> Imports { get; private set; }
        public Dictionary<QualifiedName, Struct> Structs { get; private set; }
        public Dictionary<QualifiedName, Variant> Variants { get; private set; }
        public Dictionary<QualifiedName, Interface> Interfaces { get; private set; }
        public Dictionary<QualifiedName, Function> Functions { get; private set; }
        public Dictionary<QualifiedName, List<Implementation>> Implementations { get; private set; }
        public Dictionary<Function, OxideParser.BlockContext> UnparsedBodies { get; private set; }

        public void Parse(OxideParser.Compilation_unitContext cu)
        {
            Package = cu.package().qualified_name().Parse(true);

            Imports = new List<Import>();
            Imports.Add(new Import(PrimitiveType.I32.Name, "i32"));
            Imports.Add(new Import(PrimitiveType.Bool.Name, "bool"));

            foreach (var importStmt in cu.import_stmt())
            {
                Imports.Add(new Import(
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

            UnparsedBodies = new Dictionary<Function, OxideParser.BlockContext>();

            // Parse alias
            foreach (var ctx in aliasDefs)
            {
                throw new NotImplementedException("Aliases are not implemented");
            }

            // Parse structs
            Structs = new Dictionary<QualifiedName, Struct>();
            foreach (var ctx in structDefs)
            {
                ParseStruct(ctx);
            }

            // Parse variant types
            Variants = new Dictionary<QualifiedName, Variant>();
            foreach (var ctx in variantsDefs)
            {
                ParseVariant(ctx);
            }

            // Parse interfaces
            Interfaces = new Dictionary<QualifiedName, Interface>();
            foreach (var ctx in ifaceDefs)
            {
                ParseInterface(ctx);
            }

            // Parse impls
            Implementations = new Dictionary<QualifiedName, List<Implementation>>();
            foreach (var ctx in implBlocks)
            {
                ParseImpl(ctx);
            }

            // Parse top level funcs
            Functions = new Dictionary<QualifiedName, Function>();
            foreach (var ctx in funcsDefs)
            {
                var func = ParseFunc(ctx, Package, null);
                Functions.Add(func.Name, func);
            }
        }

        private Function ParseFunc(OxideParser.Func_defContext ctx, QualifiedName owner, TypeRef thisType)
        {
            var vis = ctx.visibility().Parse();
            var funcName = ctx.name().GetText();
            var genericParams = ctx.generic_def().Parse().ToImmutableList();
            var parameters = ParseParameters(ctx.parameter(), thisType, genericParams);
            var returnType = ctx.type() != null ? ParseType(ctx.type(), genericParams) : null;
            OxideParser.BlockContext body = null;

            switch (ctx.func_body())
            {
                case OxideParser.Block_func_bodyContext blockFuncBodyContext:
                    body = blockFuncBodyContext.block();
                    break;
                case OxideParser.Empty_func_bodyContext:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            var name = owner != null
                ? new QualifiedName(owner.IsAbsolute, owner.Parts.Add(funcName))
                : new QualifiedName(false, new[] { funcName });

            var func = new Function
            {
                Name = name,
                Visibility = vis,
                Parameters = parameters.ToImmutableList(),
                GenericParams = genericParams.ToImmutableList(),
                ReturnType = returnType,
                IsExtern = ctx.EXTERN() != null,
                HasBody = body != null,
            };

            if (body != null)
            {
                UnparsedBodies.Add(func, body);
            }

            return func;
        }

        private List<Parameter> ParseParameters(OxideParser.ParameterContext[] paramCtxs, TypeRef thisType,
            ImmutableList<string> genericTypes)
        {
            var parameters = new List<Parameter>();

            for (var i = 0; i < paramCtxs.Length; i++)
            {
                var ctx = paramCtxs[i];
                switch (ctx)
                {
                    case OxideParser.Standard_parameterContext standardParameterContext:
                    {
                        parameters.Add(new Parameter
                        {
                            Name = standardParameterContext.name().GetText(),
                            IsThis = false,
                            Type = ParseType(standardParameterContext.type(), genericTypes)
                        });
                        break;
                    }
                    case OxideParser.This_parameterContext thisParameterContext:
                    {
                        if (thisType == null)
                        {
                            throw new Exception("This parameters are not allowed in this context");
                        }

                        if (i != 0)
                        {
                            throw new Exception("This parameters can only occupy the first parameter slot");
                        }

                        var (category, mutable) = thisParameterContext.type_flags().Parse();
                        parameters.Add(new Parameter
                        {
                            Name = "this",
                            IsThis = true,
                            Type = category switch
                            {
                                TypeCategory.Direct => thisType,
                                TypeCategory.Pointer => new PointerTypeRef(thisType, mutable),
                                TypeCategory.Borrow => new BorrowTypeRef(thisType, mutable),
                                TypeCategory.StrongReference => new ReferenceTypeRef(thisType, true),
                                TypeCategory.WeakReference => new ReferenceTypeRef(thisType, false),
                                _ => throw new ArgumentOutOfRangeException()
                            },
                        });
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(ctx));
                }
            }

            return parameters;
        }

        private void ParseStruct(OxideParser.Struct_defContext ctx)
        {
            var vis = ctx.visibility().Parse();
            var structName = new QualifiedName(true, Package.Parts.Add(ctx.name().GetText()));
            var genericParams = ctx.generic_def()?.Parse() ?? new List<string>();
            var fields = new List<Field>();

            foreach (var fieldDef in ctx.field_def())
            {
                var type = ParseType(fieldDef.type(), genericParams.ToImmutableList());
                fields.Add(new Field
                {
                    Name = fieldDef.name().GetText(),
                    Visibility = fieldDef.visibility().Parse(),
                    Type = type,
                    Mutable = fieldDef.MUT() != null,
                });
            }

            Structs.Add(
                structName,
                new Struct(structName, vis, genericParams.ToImmutableList(), fields.ToImmutableList())
            );
        }

        private void ParseVariant(OxideParser.Variant_defContext ctx)
        {
            var vis = ctx.visibility().Parse();
            var variantName = new QualifiedName(true, Package.Parts.Add(ctx.name().GetText()));
            var genericParams = ctx.generic_def()?.Parse() ?? new List<string>();
            var items = new Dictionary<string, VariantItem>();

            foreach (var itemDef in ctx.variant_item_def())
            {
                switch (itemDef)
                {
                    case OxideParser.Simple_variant_item_defContext simpleDef:
                    {
                        var itemName = simpleDef.name().GetText();
                        items.Add(itemName, new VariantItem
                        {
                            Name = itemName,
                            Content = null
                        });
                        break;
                    }
                    case OxideParser.Struct_variant_item_defContext structDef:
                    {
                        var itemName = structDef.name().GetText();
                        var itemFields = new List<Field>();

                        foreach (var fieldDef in structDef.field_def())
                        {
                            var type = ParseType(fieldDef.type(), genericParams.ToImmutableList());
                            itemFields.Add(new Field
                            {
                                Name = fieldDef.name().GetText(),
                                Visibility = fieldDef.visibility().Parse(Visibility.Public),
                                Type = type,
                                Mutable = fieldDef.MUT() != null,
                            });
                        }

                        items.Add(itemName, new VariantItem
                        {
                            Name = itemName,
                            Content = new Struct(
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
                        var itemFields = new List<Field>();

                        var itemId = 0;
                        foreach (var tupleCtx in tupleDef.tuple_def().tuple_item_def())
                        {
                            var type = ParseType(tupleCtx.type(), genericParams.ToImmutableList());
                            itemFields.Add(new Field
                            {
                                Name = "item" + itemId++,
                                Mutable = tupleCtx.MUT() != null,
                                Type = type,
                                Visibility = Visibility.Public
                            });
                        }

                        items.Add(itemName, new VariantItem
                        {
                            Name = itemName,
                            Content = new Struct(
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

            Variants.Add(
                variantName,
                new Variant
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
            var interfaceName = new QualifiedName(true, Package.Parts.Add(ctx.name().GetText()));
            var genericParams = ctx.generic_def()?.Parse() ?? new List<string>();

            var functions = new List<Function>();
            var thisRef = new DirectTypeRef(interfaceName, TypeSource.Concrete, ImmutableArray<TypeRef>.Empty);
            foreach (var funcDef in ctx.func_def())
            {
                functions.Add(ParseFunc(funcDef, null, thisRef));
            }

            Interfaces.Add(
                interfaceName,
                new Interface(interfaceName, vis, genericParams.ToImmutableList(), functions.ToImmutableList())
            );
        }

        private void ParseImpl(OxideParser.Impl_stmtContext ctx)
        {
            if (ctx.tgt_generics != null || ctx.iface_generics != null)
            {
                throw new NotImplementedException("Generics not implemented");
            }

            if (ctx.where() != null)
            {
                throw new NotImplementedException("Where statements not implemented");
            }

            var target = ResolveQN(ctx.tgt_name.Parse());
            var iface = ctx.iface_name != null ? ResolveQN(ctx.iface_name.Parse()) : null;

            if (!Implementations.TryGetValue(target, out var ifaces))
            {
                ifaces = new List<Implementation>();
                Implementations.Add(target, ifaces);
            }

            var functions = new List<Function>();
            var thisRef = new DirectTypeRef(target, TypeSource.Concrete, ImmutableArray<TypeRef>.Empty);

            if (ctx.impl_body() != null)
            {
                foreach (var funcDef in ctx.impl_body().func_def())
                {
                    functions.Add(ParseFunc(funcDef, null, thisRef));
                }
            }

            ifaces.Add(new Implementation(target, iface, functions.ToImmutableArray()));
        }

        public TypeRef ParseType(OxideParser.TypeContext ctx, ImmutableList<string> genericTypes)
        {
            switch (ctx)
            {
                case OxideParser.Direct_typeContext directTypeContext:
                {
                    var genericParams = new List<TypeRef>();
                    if (directTypeContext.type_generic_params() != null)
                    {
                        genericParams.AddRange(directTypeContext.type_generic_params().type()
                            .Select(x => ParseType(x, genericTypes)));
                    }

                    var rawQn = directTypeContext.qualified_name().Parse();

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

                    return new DirectTypeRef(qn, source, genericParams.ToImmutableArray());
                }
                case OxideParser.Flagged_typeContext flaggedTypeContext:
                {
                    var (category, mutable) = flaggedTypeContext.type_flags().Parse();
                    var inner = ParseType(flaggedTypeContext.type(), genericTypes);

                    switch (category)
                    {
                        case TypeCategory.Pointer:
                            return new PointerTypeRef(inner, mutable);
                        case TypeCategory.Borrow:
                            return new BorrowTypeRef(inner, mutable);
                        case TypeCategory.StrongReference:
                        case TypeCategory.WeakReference:
                            return new ReferenceTypeRef(inner, category == TypeCategory.StrongReference);
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
        }

        public (TypeSource source, QualifiedName qn) ResolveQnWithGenerics(QualifiedName rawQn,
            ImmutableList<string> genericParams)
        {
            TypeSource source;
            QualifiedName qn;
            if (!rawQn.IsAbsolute && genericParams.Contains(rawQn.Parts[0]))
            {
                source = TypeSource.Generic;
                qn = rawQn;
            }
            else
            {
                source = TypeSource.Concrete;
                qn = ResolveQN(rawQn);
            }

            return (source, qn);
        }

        public QualifiedName ResolveQN(QualifiedName qn)
        {
            if (qn.IsAbsolute)
            {
                return qn;
            }

            foreach (var import in Imports.AsEnumerable().Reverse())
            {
                if (import.Target == qn.Parts[0])
                {
                    return new QualifiedName(true, import.Source.Parts.AddRange(qn.Parts.Skip(1)));
                }
            }

            // Assume current package
            return new QualifiedName(true, Package.Parts.AddRange(qn.Parts));
        }
    }
}