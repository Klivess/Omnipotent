using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Omnipotent.Services.KliveAgent;

/// <summary>
/// Fixes the compile errors that have one obvious fix, before the model is told about them.
///
/// A failed script costs a whole model round trip (33 s on average) and the same mistakes kept coming
/// back: of 224 compile failures sampled from live conversations, 93 were one of the shapes below.
/// Each is a rule of C# scripts or of this host that ordinary C# habits break, not a judgement call:
///   • a top-level <c>using var x = …;</c> (a script reads <c>using</c> as a directive)
///   • <c>await (Task)x</c> (a script reads <c>await (</c> as a call to a method named await)
///   • a type whose namespace is not imported (BindingFlags, CancellationToken, HttpClient …)
///   • <c>name = value;</c> for a name that was never declared ("stash it for the next script")
///   • a member read, foreach or await on a value typed <c>object</c>
///   • a Task used where its result was meant (the await was left out)
///   • a cast to a type that is internal to Omnipotent
///
/// It only ever runs on a script that failed to compile, so code that works is never touched, and
/// every change is reported back with the result so the model sees what ran. Edits stay on their own
/// line: reported line numbers keep matching the script the model wrote.
/// </summary>
internal static class KliveAgentScriptRepair
{
    internal sealed record Outcome(string Code, IReadOnlyList<string> Notes);

    /// <summary>Replace <c>Length</c> characters at <c>Start</c> with <c>Text</c>.</summary>
    private readonly record struct Edit(int Start, int Length, string Text);

    private sealed class Plan
    {
        public readonly List<Edit> Edits = new();
        public readonly List<string> Notes = new();
        private readonly List<TextSpan> claimed = new();
        public readonly SortedSet<string> Usings = new(StringComparer.Ordinal);

        /// <summary>Reserves a region for one rewrite. A second rewrite of overlapping code waits for
        /// the next round, when it is judged against the script as it then stands.</summary>
        public bool Claim(TextSpan span)
        {
            foreach (var taken in claimed)
                if (taken.OverlapsWith(span) || taken.Contains(span) || span.Contains(taken) || taken == span) return false;
            claimed.Add(span);
            return true;
        }

        public void Wrap(TextSpan span, string before, string after)
        {
            Edits.Add(new Edit(span.Start, 0, before));
            Edits.Add(new Edit(span.End, 0, after));
        }
    }

    /// <summary>
    /// One round of repairs for <paramref name="code"/>, or null when nothing here can fix it.
    /// Syntax problems are fixed on their own round: until the script parses, what the compiler says
    /// about names and types cannot be trusted.
    /// </summary>
    internal static Outcome? TryRepair(string code, Compilation compilation, IReadOnlyList<Diagnostic> errors,
        KliveAgentTypeIndex types)
    {
        try
        {
            var tree = errors.Select(e => e.Location.SourceTree).FirstOrDefault(t => t != null)
                ?? compilation.SyntaxTrees.LastOrDefault();
            if (tree == null || tree.GetRoot() is not CompilationUnitSyntax root) return null;
            var text = tree.GetText();
            if (text.ToString() != code) return null;

            var plan = new Plan();
            RepairUsingDeclarations(code, root, text, errors, plan);
            RepairAwaitOfParenthesis(code, root, text, plan);
            if (plan.Edits.Count == 0)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var error in errors)
                {
                    if (!error.Location.IsInSource || error.Location.SourceTree != tree) continue;
                    try { RepairSemantic(error, root, text, model, types, plan); }
                    catch { /* one diagnostic this cannot read must not stop the others */ }
                }
            }

            foreach (var ns in plan.Usings)
                plan.Edits.Add(new Edit(FirstTokenStart(root), 0, $"using {ns}; "));
            if (plan.Edits.Count == 0) return null;

            string repaired = Apply(code, plan.Edits);
            return repaired == code ? null : new Outcome(repaired, plan.Notes.Distinct().ToList());
        }
        catch
        {
            return null;
        }
    }

    // ── syntax ──

    private static readonly System.Text.RegularExpressions.Regex UsingDirectiveLine = new(
        @"^\s*using\s+(static\s+)?[A-Za-z_][\w.]*(\s*=\s*[A-Za-z_][\w.<>,\s]*)?\s*;\s*$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// <c>using var x = …;</c> at the top level. A script reads <c>using</c> there as a directive, so the
    /// line becomes a broken directive followed by an assignment to an undeclared name; how it is broken
    /// depends on where the line sits, so the script's own parse is no guide. Parsed as a method body the
    /// same text is an ordinary using declaration, which says exactly which keyword to drop. The same
    /// pass moves a real <c>using Namespace;</c> that was written below a statement back to the top.
    /// </summary>
    private static void RepairUsingDeclarations(string code, CompilationUnitSyntax root, SourceText text,
        IReadOnlyList<Diagnostic> errors, Plan plan)
    {
        if (!code.Contains("using", StringComparison.Ordinal)) return;

        foreach (var misplaced in errors.Where(e => e.Id == "CS1529" && e.Location.IsInSource))
        {
            var line = text.Lines.GetLineFromPosition(misplaced.Location.SourceSpan.Start);
            string written = text.ToString(line.Span);
            if (!UsingDirectiveLine.IsMatch(written) || !plan.Claim(line.Span)) continue;
            plan.Edits.Add(new Edit(line.Start, line.Span.Length, string.Empty));
            plan.Edits.Add(new Edit(0, 0, written.Trim() + " "));
            plan.Notes.Add($"line {line.LineNumber + 1}: `{written.Trim()}` must come before any statement in a script, so it was moved to the top");
        }

        var body = ParseAsMethodBody(code, root, out int offset);
        var statements = body.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Body?.Statements;
        if (statements == null) return;
        foreach (var declaration in statements.Value.OfType<LocalDeclarationStatementSyntax>())
        {
            if (!declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)) continue;
            var variables = declaration.Declaration.Variables;
            if (variables.Count == 0 || variables.Any(v => v.Initializer == null || v.Identifier.IsMissing)) continue;

            int start = (declaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)
                ? declaration.AwaitKeyword.SpanStart : declaration.UsingKeyword.SpanStart) - offset;
            int end = declaration.Declaration.Type.SpanStart - offset;
            if (start < 0 || end > code.Length || end <= start) continue;
            // Only a declaration the script could not compile as written: one of the errors is on its line.
            int lineNumber = text.Lines.GetLineFromPosition(start).LineNumber;
            if (!errors.Any(e => e.Location.IsInSource && e.Location.GetLineSpan().StartLinePosition.Line == lineNumber)) continue;
            if (!plan.Claim(TextSpan.FromBounds(start, end))) continue;
            plan.Edits.Add(new Edit(start, end - start, string.Empty));
            string name = variables[0].Identifier.ValueText;
            plan.Notes.Add($"line {lineNumber + 1}: a top-level `using var` is not valid in a script, so `{name}` was declared without `using` (call {name}.Dispose() yourself if that matters)");
        }
    }

    /// <summary><c>await (Task)x</c> / <c>await (expr)</c> at the top level: a script only treats
    /// <c>await</c> as a keyword when a name or literal follows it, so <c>await (</c> becomes a call to
    /// a method named await. Parsing the same text as a method body shows where the awaited expression
    /// really ends; it is then wrapped as <c>await Aw(…)</c>.</summary>
    private static void RepairAwaitOfParenthesis(string code, CompilationUnitSyntax root, SourceText text, Plan plan)
    {
        var misread = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: "await" })
            .ToList();
        if (misread.Count == 0) return;

        var body = ParseAsMethodBody(code, root, out int offset);
        var awaits = body.DescendantNodes().OfType<AwaitExpressionSyntax>().ToList();
        foreach (var call in misread)
        {
            int position = call.Expression.SpanStart;
            var real = awaits.FirstOrDefault(a => a.AwaitKeyword.SpanStart - offset == position);
            if (real == null) continue;
            int start = real.Expression.SpanStart - offset;
            int end = real.Expression.Span.End - offset;
            if (start <= position || end > code.Length || end <= start) continue;
            if (!plan.Claim(TextSpan.FromBounds(position, end))) continue;
            plan.Wrap(TextSpan.FromBounds(start, end), "Aw(", ")");
            plan.Notes.Add($"line {LineOf(text, position)}: `await (…)` is read as a call to a method named await in a script, so it ran as `await Aw(…)`");
        }
    }

    private static SyntaxNode ParseAsMethodBody(string code, CompilationUnitSyntax root, out int offset)
    {
        // Real using directives would be statements in a method body; blank them, keeping every offset.
        var chars = code.ToCharArray();
        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (directive.SemicolonToken.IsMissing) continue;
            for (int i = directive.SpanStart; i < directive.Span.End && i < chars.Length; i++)
                if (chars[i] != '\r' && chars[i] != '\n') chars[i] = ' ';
        }
        const string prefix = "class W { async System.Threading.Tasks.Task M() {\n";
        offset = prefix.Length;
        return CSharpSyntaxTree.ParseText(prefix + new string(chars) + "\n}}",
            new CSharpParseOptions(LanguageVersion.Latest, kind: SourceCodeKind.Regular)).GetRoot();
    }

    // ── semantics ──

    private static void RepairSemantic(Diagnostic error, CompilationUnitSyntax root, SourceText text,
        SemanticModel model, KliveAgentTypeIndex types, Plan plan)
    {
        var span = error.Location.SourceSpan;
        var node = root.FindNode(span, getInnermostNodeForTie: true);
        string message = error.GetMessage();

        switch (error.Id)
        {
            case "CS0103":   // The name 'X' does not exist in the current context
                if (node is not IdentifierNameSyntax unknown) return;
                if (TryDeclareAssignedName(unknown, text, plan)) return;
                // A type used for a static member (BindingFlags.Public, SHA256.HashData) whose namespace is not imported.
                if (unknown.Parent is MemberAccessExpressionSyntax owner && owner.Expression == unknown)
                    TryImport(unknown.Identifier.ValueText, types, plan);
                return;

            case "CS0246":   // The type or namespace name 'X' could not be found
                if (TypeNameOf(node) is { } missing) TryImport(missing, types, plan);
                return;

            case "CS1061" when StartsWithObjectType(message):
                if (message.Contains("'GetAwaiter'", StringComparison.Ordinal))
                {
                    // `await x` where x is typed object: it may or may not be a task at run time.
                    if (node.AncestorsAndSelf().OfType<AwaitExpressionSyntax>().FirstOrDefault() is { } awaited
                        && plan.Claim(awaited.Span))
                    {
                        plan.Wrap(awaited.Expression.Span, "Aw(", ")");
                        plan.Notes.Add($"line {LineOf(text, awaited.SpanStart)}: awaited a value typed `object`, so it ran as `await Aw(…)` (awaits it if it is a task, otherwise passes it through)");
                    }
                    return;
                }
                if (ReceiverOf(node) is { } receiver && plan.Claim(receiver.Span))
                {
                    plan.Wrap(receiver.Span, "Dyn(", ")");
                    plan.Notes.Add($"line {LineOf(text, receiver.SpanStart)}: `{Clip(receiver.ToString())}` is typed `object`, so its member was read as `Dyn({Clip(receiver.ToString())}).{MemberNameOf(node)}` (looked up when the script runs)");
                }
                return;

            case "CS1061" when StartsWithTaskType(message):
                // A member of the result, read off the task: the await was left out.
                if (ReceiverOf(node) is { } task && CanAwaitAt(task) && plan.Claim(task.Span))
                {
                    plan.Wrap(task.Span, "(await ", ")");
                    plan.Notes.Add($"line {LineOf(text, task.SpanStart)}: `{Clip(task.ToString())}` returns a Task, so `await` was added before reading `.{MemberNameOf(node)}`");
                }
                return;

            case "CS1579" when message.Contains("type 'object'", StringComparison.Ordinal):   // foreach over object
                if (node is ExpressionSyntax collection && collection.Parent is ForEachStatementSyntax loop
                    && loop.Expression == collection && plan.Claim(collection.Span))
                {
                    plan.Wrap(collection.Span, "Dyn(", ")");
                    plan.Notes.Add($"line {LineOf(text, collection.SpanStart)}: `{Clip(collection.ToString())}` is typed `object`, so the loop ran over `Dyn(…)`");
                }
                return;

            case "CS0021" when message.Contains("type 'object'", StringComparison.Ordinal):   // indexing object
                if (node.AncestorsAndSelf().OfType<ElementAccessExpressionSyntax>().FirstOrDefault() is { } indexed
                    && plan.Claim(indexed.Expression.Span))
                {
                    plan.Wrap(indexed.Expression.Span, "Dyn(", ")");
                    plan.Notes.Add($"line {LineOf(text, indexed.SpanStart)}: `{Clip(indexed.Expression.ToString())}` is typed `object`, so it was indexed as `Dyn(…)[…]`");
                }
                return;

            case "CS0030":   // Cannot convert type 'Task<…>' to 'X'
                if (node.AncestorsAndSelf().OfType<CastExpressionSyntax>().FirstOrDefault() is { } cast
                    && IsTask(model.GetTypeInfo(cast.Expression).Type) && !cast.Type.ToString().Contains("Task", StringComparison.Ordinal)
                    && CanAwaitAt(cast) && plan.Claim(cast.Expression.Span))
                {
                    plan.Wrap(cast.Expression.Span, "(await ", ")");
                    plan.Notes.Add($"line {LineOf(text, cast.SpanStart)}: `{Clip(cast.Expression.ToString())}` returns a Task, so `await` was added before the cast");
                }
                return;

            case "CS0029":   // Cannot implicitly convert type 'Task<T>' to 'T'
            case "CS0266":
                if (node is ExpressionSyntax value && IsTask(model.GetTypeInfo(value).Type)
                    && !message.Split(" to ", 2).Last().Contains("Task", StringComparison.Ordinal)
                    && CanAwaitAt(value) && plan.Claim(value.Span))
                {
                    plan.Wrap(value.Span, "(await ", ")");
                    plan.Notes.Add($"line {LineOf(text, value.SpanStart)}: `{Clip(value.ToString())}` returns a Task, so `await` was added");
                }
                return;

            case "CS0122":   // 'X' is inaccessible due to its protection level
                RepairInaccessible(node, text, model, plan);
                return;

            case "CS1977":   // Cannot use a lambda expression as an argument to a dynamically dispatched operation
                RepairLambdaToDynamicCall(node, text, plan);
                return;
        }
    }

    /// <summary>
    /// A lambda passed to a call on a dynamic value (<c>Dyn(store).Mutate(s =&gt; { … })</c>) has no type the
    /// compiler can pick. Its shape picks one: a block with no value returned is an Action, one that
    /// returns a value is a Func, each over dynamic. The run-time wrapper adapts that to the delegate
    /// the method really takes. An expression lambda that is itself a call is left to the model, since
    /// whether that call returns anything cannot be read off the text.
    /// </summary>
    private static void RepairLambdaToDynamicCall(SyntaxNode node, SourceText text, Plan plan)
    {
        var lambda = node.AncestorsAndSelf().OfType<LambdaExpressionSyntax>().FirstOrDefault();
        if (lambda == null || lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword)) return;
        int parameters = lambda switch
        {
            SimpleLambdaExpressionSyntax => 1,
            ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.Count,
            _ => -1,
        };
        if (parameters < 0 || parameters > 4) return;

        bool returnsValue;
        if (lambda.Block is { } block)
            returnsValue = block.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
                .OfType<ReturnStatementSyntax>().Any(r => r.Expression != null);
        else if (lambda.ExpressionBody is InvocationExpressionSyntax or AwaitExpressionSyntax or null)
            return;
        else
            returnsValue = true;

        string arguments = string.Join(", ", Enumerable.Repeat("dynamic", parameters + (returnsValue ? 1 : 0)));
        string type = returnsValue ? $"Func<{arguments}>" : parameters == 0 ? "Action" : $"Action<{arguments}>";
        if (!plan.Claim(lambda.Span)) return;
        plan.Wrap(lambda.Span, $"({type})(", ")");
        plan.Notes.Add($"line {LineOf(text, lambda.SpanStart)}: a lambda passed to a dynamic call needs its type written, so it ran as `({type})(…)`");
    }

    /// <summary><c>name = value;</c> as a top-level statement, for a name that does not exist: the script
    /// meant to keep the value for a later script.</summary>
    private static bool TryDeclareAssignedName(IdentifierNameSyntax name, SourceText text, Plan plan)
    {
        if (name.Parent is not AssignmentExpressionSyntax assignment || assignment.Left != name) return false;
        if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) return false;
        if (assignment.Parent is not ExpressionStatementSyntax { Parent: GlobalStatementSyntax }) return false;
        var value = assignment.Right;
        // `var` cannot take its type from a lambda, a method group or a bare collection or tuple literal.
        if (value is AnonymousFunctionExpressionSyntax or CollectionExpressionSyntax or ImplicitArrayCreationExpressionSyntax
            or InitializerExpressionSyntax) return false;
        bool untyped = value.IsKind(SyntaxKind.NullLiteralExpression) || value.IsKind(SyntaxKind.DefaultLiteralExpression);
        if (!plan.Claim(name.Span)) return true;
        plan.Edits.Add(new Edit(name.SpanStart, 0, untyped ? "object " : "var "));
        plan.Notes.Add($"line {LineOf(text, name.SpanStart)}: `{name.Identifier.ValueText}` did not exist, so it was declared here (`var {name.Identifier.ValueText} = …`); it is now a variable later scripts can use");
        return true;
    }

    private static void TryImport(string typeName, KliveAgentTypeIndex types, Plan plan)
    {
        if (typeName.Length == 0 || !char.IsUpper(typeName[0])) return;
        var ns = types.PreferredNamespaceOf(typeName);
        if (ns == null) return;
        if (plan.Usings.Add(ns)) plan.Notes.Add($"added `using {ns};` for `{typeName}`");
    }

    /// <summary>An inaccessible type or member. A cast to an internal type becomes <c>Dyn(value)</c>; an
    /// internal type used as a generic argument, a declared type or a lambda parameter type becomes
    /// <c>dynamic</c>; a non-public instance member is read through <c>Dyn(receiver)</c>.</summary>
    private static void RepairInaccessible(SyntaxNode node, SourceText text, SemanticModel model, Plan plan)
    {
        var info = model.GetSymbolInfo(node);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

        if (symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol)
        {
            if (symbol.IsStatic) return;
            if (ReceiverOf(node) is { } receiver && plan.Claim(receiver.Span))
            {
                plan.Wrap(receiver.Span, "Dyn(", ")");
                plan.Notes.Add($"line {LineOf(text, receiver.SpanStart)}: `{symbol.Name}` is not public, so it was reached as `Dyn({Clip(receiver.ToString())}).{symbol.Name}`");
            }
            return;
        }
        if (symbol is not INamedTypeSymbol hidden) return;

        // The whole written type: Omnipotent.Services.X.Engine.Store, not just its last name.
        SyntaxNode type = node;
        while (type.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax or NullableTypeSyntax) type = type.Parent;

        switch (type.Parent)
        {
            case CastExpressionSyntax cast when cast.Type == type:
                if (!plan.Claim(cast.Span)) return;
                plan.Edits.Add(new Edit(cast.OpenParenToken.SpanStart, cast.Expression.SpanStart - cast.OpenParenToken.SpanStart, "Dyn("));
                plan.Edits.Add(new Edit(cast.Expression.Span.End, 0, ")"));
                plan.Notes.Add($"line {LineOf(text, cast.SpanStart)}: `{hidden.Name}` is internal to Omnipotent and cannot be named in a script, so the cast became `Dyn(…)`: call its members on that (`x.Method(…)`, `x.Property`)");
                return;

            case BinaryExpressionSyntax asCast when asCast.IsKind(SyntaxKind.AsExpression) && asCast.Right == type:
                if (!plan.Claim(asCast.Span)) return;
                plan.Edits.Add(new Edit(asCast.Left.SpanStart, 0, "Dyn("));
                plan.Edits.Add(new Edit(asCast.Left.Span.End, asCast.Span.End - asCast.Left.Span.End, ")"));
                plan.Notes.Add($"line {LineOf(text, asCast.SpanStart)}: `{hidden.Name}` is internal to Omnipotent and cannot be named in a script, so `as {hidden.Name}` became `Dyn(…)`");
                return;

            case TypeArgumentListSyntax:
            case VariableDeclarationSyntax:
            case ParameterSyntax:
            case DeclarationExpressionSyntax:
                if (!plan.Claim(type.Span)) return;
                plan.Edits.Add(new Edit(type.SpanStart, type.Span.Length, "dynamic"));
                plan.Notes.Add($"line {LineOf(text, type.SpanStart)}: `{hidden.Name}` is internal to Omnipotent and cannot be named in a script, so it was written as `dynamic`");
                return;
        }
    }

    // ── helpers ──

    private static bool StartsWithObjectType(string message) =>
        message.StartsWith("'object' ", StringComparison.Ordinal) || message.StartsWith("'object?' ", StringComparison.Ordinal);

    private static bool StartsWithTaskType(string message) =>
        message.StartsWith("'Task<", StringComparison.Ordinal) || message.StartsWith("'Task' ", StringComparison.Ordinal)
        || message.StartsWith("'ValueTask", StringComparison.Ordinal)
        || message.StartsWith("'System.Threading.Tasks.Task", StringComparison.Ordinal);

    private static bool IsTask(ITypeSymbol? type) =>
        type is { Name: "Task" or "ValueTask" }
        && type.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks";

    /// <summary>An await can be added here unless the code sits in a lambda or local function that is not async.</summary>
    private static bool CanAwaitAt(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case AnonymousFunctionExpressionSyntax lambda:
                    return lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword);
                case LocalFunctionStatementSyntax local:
                    return local.Modifiers.Any(SyntaxKind.AsyncKeyword);
                case MethodDeclarationSyntax method:
                    return method.Modifiers.Any(SyntaxKind.AsyncKeyword);
                case QueryExpressionSyntax or LockStatementSyntax:
                    return false;
            }
        }
        return true;
    }

    /// <summary>The expression a member name is read from: <c>a.b</c> gives <c>a</c>, <c>a?.b</c> gives <c>a</c>.</summary>
    private static ExpressionSyntax? ReceiverOf(SyntaxNode memberName)
    {
        foreach (var node in memberName.AncestorsAndSelf())
        {
            switch (node)
            {
                case MemberAccessExpressionSyntax access when access == memberName || access.Name.Span.Contains(memberName.Span):
                    return access.Expression;
                // `a?.b`: the compiler reports the whole `.b`, so the node may be the binding itself.
                case MemberBindingExpressionSyntax binding when binding == memberName || binding.Name.Span.Contains(memberName.Span):
                    return binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>()
                        .FirstOrDefault(c => c.WhenNotNull.Span.Contains(binding.Span))?.Expression;
                case StatementSyntax:
                    return null;
            }
        }
        return null;
    }

    private static string MemberNameOf(SyntaxNode node) => node switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        _ => node.ToString(),
    };

    private static string? TypeNameOf(SyntaxNode node) => node switch
    {
        GenericNameSyntax generic => generic.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax => null,   // already namespace-qualified: an import cannot help
        _ => null,
    };

    private static int FirstTokenStart(CompilationUnitSyntax root)
    {
        var first = root.GetFirstToken();
        return first.IsKind(SyntaxKind.None) ? 0 : first.SpanStart;
    }

    private static int LineOf(SourceText text, int position) =>
        text.Lines.GetLineFromPosition(Math.Clamp(position, 0, text.Length)).LineNumber + 1;

    private static string Clip(string expression)
    {
        string oneLine = expression.Replace("\r", "").Replace('\n', ' ');
        return oneLine.Length <= 40 ? oneLine : oneLine[..39] + "…";
    }

    /// <summary>Applies edits back to front so earlier offsets stay valid. Insertions at one position
    /// keep the order they were added in.</summary>
    private static string Apply(string code, List<Edit> edits)
    {
        var ordered = edits.Select((edit, index) => (edit, index))
            .OrderByDescending(x => x.edit.Start).ThenByDescending(x => x.index)
            .Select(x => x.edit);
        var result = new System.Text.StringBuilder(code);
        foreach (var edit in ordered)
        {
            if (edit.Start < 0 || edit.Start + edit.Length > result.Length) continue;
            result.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        }
        return result.ToString();
    }
}
