using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Omnipotent.Services.OmniTrader.Strategy;
using Omnipotent.Services.OmniTrader.Studio;
using System.Reflection;
using System.Runtime.Loader;

namespace Omnipotent.Services.OmniTrader.Scripting
{
    public sealed class ScriptCompileResult
    {
        public bool Success { get; init; }
        public List<StudioDiagnostic> Diagnostics { get; init; } = new();
        public Type? StrategyType { get; init; }
        public string? DisplayName { get; init; }
        public string? Description { get; init; }
        public bool IsUniverse { get; init; }
        internal ScriptLoadContext? Context { get; init; }
        public double ElapsedMs { get; init; }
    }

    /// <summary>Collectible context for one compiled script version. Omnipotent itself resolves from the
    /// default context, so the script's TradingStrategy is the engine's TradingStrategy.</summary>
    internal sealed class ScriptLoadContext : AssemblyLoadContext
    {
        public ScriptLoadContext(string name) : base(name, isCollectible: true) { }
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }

    /// <summary>
    /// Compiles C# strategy scripts in-process. A script is ordinary C# — one or more classes, at
    /// least one deriving from <see cref="ScriptStrategy"/>, <see cref="ScriptUniverseStrategy"/> or
    /// <see cref="TradingStrategy"/> — compiled against the trading API only. The sandbox rejects I/O,
    /// networking, reflection, processes, threads, unsafe code, goto and finalizers, and anything in
    /// Omnipotent outside the trading namespaces; the guard rewriter then bounds every loop and call
    /// depth, so a buggy or hostile script fails its own run instead of the host.
    /// </summary>
    public static class ScriptCompiler
    {
        private const string GlobalUsings = @"
global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Text;
global using System.Globalization;
global using System.Threading;
global using System.Threading.Tasks;
global using Omnipotent.Services.OmniTrader.Contracts;
global using Omnipotent.Services.OmniTrader.Strategy;
global using Omnipotent.Services.OmniTrader.Strategy.Params;
global using Omnipotent.Services.OmniTrader.Technicals;
global using Omnipotent.Services.OmniTrader.Scripting;
";

        /// <summary>Namespaces a script may use (prefix match on the symbol's namespace).</summary>
        private static readonly string[] AllowedNamespaces =
        {
            "System.Collections", "System.Linq", "System.Text", "System.Globalization", "System.Numerics", "System.Buffers",
            "System.Threading.Tasks",
            "Omnipotent.Services.OmniTrader.Contracts", "Omnipotent.Services.OmniTrader.Strategy",
            "Omnipotent.Services.OmniTrader.Technicals", "Omnipotent.Services.OmniTrader.Scripting",
        };

        /// <summary>Types in the root System namespace (or allowed ones) that a script may not touch.</summary>
        private static readonly HashSet<string> DeniedTypes = new(StringComparer.Ordinal)
        {
            "System.Environment", "System.AppDomain", "System.Activator", "System.GC", "System.Console", "System.Type",
            "System.Runtime.CompilerServices.Unsafe", "System.Threading.Tasks.Parallel", "System.Threading.Tasks.TaskFactory",
            "System.Threading.Tasks.TaskScheduler", "System.AppContext", "System.Delegate", "System.MulticastDelegate",
            "System.Buffers.ArrayPool`1",
            // The guard and the compiler are for the host, not the script.
            "Omnipotent.Services.OmniTrader.Scripting.ScriptGuard", "Omnipotent.Services.OmniTrader.Scripting.ScriptCompiler",
        };

        private static readonly HashSet<string> DeniedMembers = new(StringComparer.Ordinal)
        {
            "System.Threading.Tasks.Task.Run", "System.Threading.Tasks.Task.Factory", "System.Threading.Tasks.Task.Delay",
            "System.Threading.Tasks.Task.Yield", "System.Object.GetType", "System.Threading.Tasks.Task.WaitAll", "System.Threading.Tasks.Task.Wait",
            // Wall-clock time differs between a backtest and live; strategies read the bar's Time.
            "System.DateTime.Now", "System.DateTime.UtcNow", "System.DateTime.Today", "System.DateTimeOffset.Now", "System.DateTimeOffset.UtcNow",
        };

        /// <summary>System.Threading types a script needs for signatures; everything else there is denied.</summary>
        private static readonly HashSet<string> AllowedThreadingTypes = new(StringComparer.Ordinal)
        {
            "System.Threading.CancellationToken"
        };

        private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(BuildReferences);

        private static IReadOnlyList<MetadataReference> BuildReferences()
        {
            var refs = new List<MetadataReference>();
            var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in tpa)
            {
                string file = Path.GetFileNameWithoutExtension(path);
                if (file is "netstandard" or "mscorlib" || file.StartsWith("System.", StringComparison.Ordinal) || file == "System")
                {
                    try { refs.Add(MetadataReference.CreateFromFile(path)); } catch { }
                }
            }
            refs.Add(MetadataReference.CreateFromFile(typeof(TradingStrategy).Assembly.Location));
            return refs;
        }

        public static ScriptCompileResult Compile(string source, string assemblyName)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var diagnostics = new List<StudioDiagnostic>();
            if (string.IsNullOrWhiteSpace(source))
                return Fail(diagnostics, "The script is empty.", sw);
            if (source.Length > 200_000)
                return Fail(diagnostics, "The script is larger than 200 KB.", sw);

            var parse = new CSharpParseOptions(LanguageVersion.Latest);
            var usings = CSharpSyntaxTree.ParseText(GlobalUsings, parse, path: "usings.cs");
            var tree = CSharpSyntaxTree.ParseText(source, parse, path: "strategy.cs");
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release, allowUnsafe: false,
                nullableContextOptions: NullableContextOptions.Disable);

            // 1. Compile what the author wrote, for honest line numbers.
            var compilation = CSharpCompilation.Create(assemblyName, new[] { usings, tree }, References.Value, options);
            foreach (var d in compilation.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning && d.Location.SourceTree == tree))
                diagnostics.Add(ToDiagnostic(d));
            if (diagnostics.Any(d => d.Severity == "error"))
                return new ScriptCompileResult { Success = false, Diagnostics = diagnostics, ElapsedMs = sw.Elapsed.TotalMilliseconds };

            // 2. Sandbox: walk the semantic model.
            var model = compilation.GetSemanticModel(tree);
            Inspect(tree, model, diagnostics);
            if (diagnostics.Any(d => d.Severity == "error"))
                return new ScriptCompileResult { Success = false, Diagnostics = diagnostics, ElapsedMs = sw.Elapsed.TotalMilliseconds };

            // 3. Guard injection, then the build that actually runs.
            var guarded = (CSharpSyntaxTree)CSharpSyntaxTree.Create((CSharpSyntaxNode)new GuardRewriter().Visit(tree.GetRoot()), parse, "strategy.cs");
            var final = CSharpCompilation.Create(assemblyName, new[] { usings, guarded }, References.Value, options);
            using var ms = new MemoryStream();
            var emit = final.Emit(ms);
            if (!emit.Success)
            {
                foreach (var d in emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                    diagnostics.Add(ToDiagnostic(d, internalError: true));
                return new ScriptCompileResult { Success = false, Diagnostics = diagnostics, ElapsedMs = sw.Elapsed.TotalMilliseconds };
            }

            ms.Position = 0;
            var context = new ScriptLoadContext(assemblyName);
            Assembly assembly;
            try { assembly = context.LoadFromStream(ms); }
            catch (Exception ex)
            {
                context.Unload();
                return Fail(diagnostics, "Load failed: " + ex.Message, sw);
            }

            var candidates = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic && typeof(TradingStrategy).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null)
                .ToList();
            if (candidates.Count == 0)
            {
                context.Unload();
                return Fail(diagnostics, "No public, non-abstract class derives from ScriptStrategy, ScriptUniverseStrategy or TradingStrategy (with a parameterless constructor).", sw);
            }
            if (candidates.Count > 1)
                diagnostics.Add(new StudioDiagnostic { Severity = "info", Message = $"{candidates.Count} strategy classes found; using {candidates[0].Name}." });

            var type = candidates.OrderByDescending(t => t.GetCustomAttribute<TradingStrategyAttribute>() != null).First();
            var attr = type.GetCustomAttribute<TradingStrategyAttribute>();
            bool universe = typeof(ScriptUniverseStrategy).IsAssignableFrom(type) || (attr?.RequiresUniverse ?? false);
            return new ScriptCompileResult
            {
                Success = true,
                Diagnostics = diagnostics,
                StrategyType = type,
                DisplayName = attr?.Name,
                Description = attr?.Description,
                IsUniverse = universe,
                Context = context,
                ElapsedMs = sw.Elapsed.TotalMilliseconds
            };
        }

        private static ScriptCompileResult Fail(List<StudioDiagnostic> diagnostics, string message, System.Diagnostics.Stopwatch sw)
        {
            diagnostics.Add(new StudioDiagnostic { Message = message, Severity = "error" });
            return new ScriptCompileResult { Success = false, Diagnostics = diagnostics, ElapsedMs = sw.Elapsed.TotalMilliseconds };
        }

        private static StudioDiagnostic ToDiagnostic(Diagnostic d, bool internalError = false)
        {
            var span = d.Location.GetLineSpan();
            return new StudioDiagnostic
            {
                Message = internalError ? "Internal: " + d.GetMessage() : d.GetMessage(),
                Severity = d.Severity == DiagnosticSeverity.Error ? "error" : d.Severity == DiagnosticSeverity.Warning ? "warning" : "info",
                Line = span.StartLinePosition.Line + 1,
                Column = span.StartLinePosition.Character + 1,
                EndLine = span.EndLinePosition.Line + 1,
                EndColumn = span.EndLinePosition.Character + 1,
                Code = d.Id
            };
        }

        // ── sandbox ──────────────────────────────────────────────────────────────

        private static void Inspect(SyntaxTree tree, SemanticModel model, List<StudioDiagnostic> diagnostics)
        {
            var root = tree.GetRoot();
            var reported = new HashSet<(int, string)>();

            void Deny(SyntaxNode node, string message)
            {
                var span = node.GetLocation().GetLineSpan();
                if (!reported.Add((span.StartLinePosition.Line, message))) return;
                diagnostics.Add(new StudioDiagnostic
                {
                    Message = message, Severity = "error", Code = "SANDBOX",
                    Line = span.StartLinePosition.Line + 1, Column = span.StartLinePosition.Character + 1,
                    EndLine = span.EndLinePosition.Line + 1, EndColumn = span.EndLinePosition.Character + 1
                });
            }

            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case GotoStatementSyntax: Deny(node, "goto is not allowed."); continue;
                    case UnsafeStatementSyntax or FixedStatementSyntax: Deny(node, "unsafe code is not allowed."); continue;
                    case StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax: Deny(node, "stackalloc is not allowed."); continue;
                    case DestructorDeclarationSyntax: Deny(node, "Finalizers are not allowed."); continue;
                    case ConstructorDeclarationSyntax ctor when ctor.Modifiers.Any(SyntaxKind.StaticKeyword): Deny(node, "Static constructors are not allowed."); continue;
                    case MethodDeclarationSyntax m when m.Modifiers.Any(SyntaxKind.ExternKeyword): Deny(node, "extern methods are not allowed."); continue;
                    case AttributeSyntax attr when attr.Name.ToString().Contains("DllImport") || attr.Name.ToString().Contains("LibraryImport"): Deny(node, "Native interop is not allowed."); continue;
                    case UsingDirectiveSyntax u:
                    {
                        string ns = u.NamespaceOrType?.ToString() ?? "";
                        if (!IsAllowedNamespace(ns) && ns != "System") Deny(node, $"using {ns} is not allowed in a strategy.");
                        continue;
                    }
                }

                if (node is not (IdentifierNameSyntax or GenericNameSyntax or QualifiedNameSyntax or MemberAccessExpressionSyntax
                        or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or InvocationExpressionSyntax)) continue;

                ISymbol? symbol;
                try { symbol = model.GetSymbolInfo(node).Symbol ?? model.GetSymbolInfo(node).CandidateSymbols.FirstOrDefault(); }
                catch { continue; }
                if (symbol == null) continue;

                if (node is IdentifierNameSyntax id && id.IsVar) continue;
                if (symbol.Kind == SymbolKind.DynamicType) { Deny(node, "dynamic is not allowed."); continue; }
                if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol or ILabelSymbol or IDiscardSymbol) continue;

                // Members declared by the script itself are always fine.
                if (symbol.ContainingAssembly is { } asm && SymbolEqualityComparer.Default.Equals(asm, model.Compilation.Assembly)) continue;
                if (symbol is INamedTypeSymbol self && self.ContainingAssembly != null && SymbolEqualityComparer.Default.Equals(self.ContainingAssembly, model.Compilation.Assembly)) continue;

                var reason = Check(symbol);
                if (reason != null) Deny(node, reason);
            }
        }

        private static string? Check(ISymbol symbol)
        {
            if (symbol is INamespaceSymbol ns)
            {
                string name = ns.ToDisplayString();
                return name.Length == 0 || name == "System" || IsAllowedNamespace(name) || "Omnipotent.Services.OmniTrader".StartsWith(name, StringComparison.Ordinal)
                    || "System.Collections.Generic".StartsWith(name, StringComparison.Ordinal) || "System.Threading.Tasks".StartsWith(name, StringComparison.Ordinal)
                    ? null : $"{name} is not available to strategies.";
            }

            var type = symbol as ITypeSymbol ?? symbol.ContainingType;
            if (type is IArrayTypeSymbol array) type = array.ElementType;
            if (type is IPointerTypeSymbol or IFunctionPointerTypeSymbol) return "Pointers are not allowed.";
            if (type == null) return null;
            if (type is ITypeParameterSymbol) return null;

            string typeName = Definition(type);
            if (DeniedTypes.Contains(typeName)) return $"{type.ToDisplayString()} is not available to strategies.";
            if (symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol)
            {
                string member = $"{typeName}.{symbol.Name}";
                if (DeniedMembers.Contains(member))
                    return member.StartsWith("System.DateTime", StringComparison.Ordinal)
                        ? $"{symbol.ContainingType?.Name}.{symbol.Name} reads the wall clock; use Time (the bar's timestamp)."
                        : $"{symbol.ContainingType?.Name}.{symbol.Name} is not available to strategies.";
            }

            string nsName = type.ContainingNamespace?.ToDisplayString() ?? "";
            if (nsName == "System")
            {
                // Root System types are fine except the denied ones above (and anything delegate-to-pointer).
                return null;
            }
            if (nsName == "System.Threading" && AllowedThreadingTypes.Contains(typeName)) return null;
            if (IsAllowedNamespace(nsName)) return null;
            if (nsName.Length == 0) return null;
            return $"{type.ToDisplayString()} ({nsName}) is not available to strategies.";
        }

        private static string Definition(ITypeSymbol type)
        {
            var original = type.OriginalDefinition;
            string ns = original.ContainingNamespace?.ToDisplayString() ?? "";
            string name = original.MetadataName;
            var outer = original.ContainingType;
            while (outer != null) { name = outer.MetadataName + "+" + name; outer = outer.ContainingType; }
            return ns.Length == 0 ? name : ns + "." + name;
        }

        private static bool IsAllowedNamespace(string ns)
            => AllowedNamespaces.Any(a => ns == a || ns.StartsWith(a + ".", StringComparison.Ordinal));

        // ── guard injection ──────────────────────────────────────────────────────

        private sealed class GuardRewriter : CSharpSyntaxRewriter
        {
            private static StatementSyntax Tick() => SyntaxFactory.ParseStatement("global::Omnipotent.Services.OmniTrader.Scripting.ScriptGuard.Tick();");
            private static StatementSyntax Enter() => SyntaxFactory.ParseStatement("using var __omniGuard = global::Omnipotent.Services.OmniTrader.Scripting.ScriptGuard.Enter();");

            private static StatementSyntax WithTick(StatementSyntax body)
            {
                var block = body as BlockSyntax ?? SyntaxFactory.Block(body);
                return block.WithStatements(block.Statements.Insert(0, Tick()));
            }

            public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
            {
                var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
                return visited.WithStatement(WithTick(visited.Statement));
            }

            public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
            {
                var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
                return visited.WithStatement(WithTick(visited.Statement));
            }

            public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
            {
                var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
                return visited.WithStatement(WithTick(visited.Statement));
            }

            public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
            {
                var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
                return visited.WithStatement(WithTick(visited.Statement));
            }

            public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
            {
                var visited = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
                return visited.WithStatement(WithTick(visited.Statement));
            }

            public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
                if (visited.Modifiers.Any(SyntaxKind.AsyncKeyword) || visited.Modifiers.Any(SyntaxKind.AbstractKeyword)) return visited;
                if (visited.Body != null) return visited.WithBody(visited.Body.WithStatements(visited.Body.Statements.Insert(0, Enter())));
                if (visited.ExpressionBody != null)
                {
                    bool isVoid = visited.ReturnType is PredefinedTypeSyntax p && p.Keyword.IsKind(SyntaxKind.VoidKeyword);
                    if (ContainsYield(visited)) return visited;
                    StatementSyntax stmt = isVoid
                        ? SyntaxFactory.ExpressionStatement(visited.ExpressionBody.Expression)
                        : SyntaxFactory.ReturnStatement(visited.ExpressionBody.Expression);
                    return visited.WithExpressionBody(null).WithSemicolonToken(default).WithBody(SyntaxFactory.Block(Enter(), stmt));
                }
                return visited;
            }

            public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
            {
                var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
                if (visited.Modifiers.Any(SyntaxKind.AsyncKeyword) || visited.Body == null) return visited;
                return visited.WithBody(visited.Body.WithStatements(visited.Body.Statements.Insert(0, Enter())));
            }

            private static bool ContainsYield(SyntaxNode node) => node.DescendantNodes().OfType<YieldStatementSyntax>().Any();
        }
    }
}
