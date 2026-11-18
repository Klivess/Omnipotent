using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Omnipotent.Services.KliveAgent;

/// <summary>
/// Turns a failed script into the message the model reads.
///
/// A quarter of compile failures were a guessed member, type or overload, and the old message only
/// repeated the compiler's "does not contain a definition": the model then spent another step (33 s)
/// looking the real name up, or guessed again. The compiler already knows the answer, so the message
/// now carries it: the closest real members with their signatures, the real overloads, or the namespace
/// a type lives in. A runtime error names the script line it happened on.
/// </summary>
internal static class KliveAgentScriptDiagnostics
{
    private const int MaxShown = 8;
    private const int MaxExplained = 4;

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.MinimallyQualifiedFormat;

    internal static string FormatCompileErrors(string code, IReadOnlyList<Diagnostic> errors, Compilation? compilation,
        KliveAgentTypeIndex? types, IReadOnlyList<string>? repairs = null)
    {
        var lines = (code ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        sb.AppendLine($"Compilation failed — {errors.Count} error(s); nothing ran. Fix the exact line(s) below — don't just re-run the same call:");
        if (repairs is { Count: > 0 })
        {
            sb.AppendLine("Already fixed for you (keep these in your next version; the line numbers below are for the script with them applied):");
            foreach (var repair in repairs) sb.AppendLine("  • " + repair);
        }

        SyntaxNode? root = null;
        SemanticModel? model = null;
        try
        {
            var tree = errors.Select(e => e.Location.SourceTree).FirstOrDefault(t => t != null);
            if (tree != null && compilation != null)
            {
                root = tree.GetRoot();
                model = compilation.GetSemanticModel(tree);
            }
        }
        catch { /* explanations are extra: the errors themselves are always shown */ }

        int explained = 0;
        foreach (var d in errors.Take(MaxShown))
        {
            var span = d.Location.GetLineSpan();
            int lineNo = span.StartLinePosition.Line;    // 0-based
            int col = span.StartLinePosition.Character;  // 0-based
            sb.AppendLine($"[{d.Id}] line {lineNo + 1}:{col + 1} — {Shorten(d.GetMessage())}");

            if (d.Location.IsInSource && lineNo >= 0 && lineNo < lines.Length)
            {
                // Preceding lines show where the wrong assumption about a variable's type was formed.
                for (int ctx = Math.Max(0, lineNo - 3); ctx < lineNo; ctx++)
                    sb.AppendLine($"  {(ctx + 1).ToString().PadLeft(4)} | {lines[ctx]}");

                var srcLine = lines[lineNo];
                int spanLen = Math.Max(1, d.Location.SourceSpan.Length);
                int caretLen = Math.Min(spanLen, Math.Max(1, srcLine.Length - col));
                sb.AppendLine($"  {(lineNo + 1).ToString().PadLeft(4)} | {srcLine}");
                sb.AppendLine($"  {new string(' ', 4)} | {new string(' ', Math.Max(0, col))}{new string('^', caretLen)}");
            }

            if (explained >= MaxExplained) continue;
            string? explanation = null;
            try { explanation = Explain(d, root, model, types); }
            catch { /* never let an explanation hide the error */ }
            if (string.IsNullOrWhiteSpace(explanation)) continue;
            explained++;
            sb.AppendLine("       → " + explanation);
        }
        if (errors.Count > MaxShown)
            sb.AppendLine($"  … and {errors.Count - MaxShown} more error(s).");

        if (errors.Any(d => d.GetMessage().Contains("'char' to 'string'", StringComparison.Ordinal)))
            sb.AppendLine("Hint: the search/read tools (FindFiles, SearchCode, SearchCodeRegex, SearchCodeHybrid, ReadFile, GetRepoMap, GetMethodDocumentation) each return ONE formatted string, not a list — Log() the whole string; do NOT foreach over it (iterating a string yields chars).");
        if (errors.Any(d => d.GetMessage().Contains("does not contain a definition for 'Length'", StringComparison.Ordinal)))
            sb.AppendLine("Hint: use .Count for List<T>/collections (e.g. schema.Methods.Count); .Length is only for arrays and strings.");
        if (errors.Any(d => d.GetMessage().Contains("does not contain a definition for 'GetTypeName'", StringComparison.Ordinal)))
            sb.AppendLine("Hint: there is no GetTypeName(); use ex.GetType().Name to get an exception's type name.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>The compiler's sentence without the tail that never helps here.</summary>
    private static string Shorten(string message)
    {
        int cut = message.IndexOf(" and no accessible extension method", StringComparison.Ordinal);
        if (cut > 0) return message[..cut];
        return message.Replace(" (are you missing a using directive or an assembly reference?)", "")
            .Replace(" (are you missing an assembly reference?)", "");
    }

    // ── explanations ──

    private static string? Explain(Diagnostic d, SyntaxNode? root, SemanticModel? model, KliveAgentTypeIndex? types)
    {
        string message = d.GetMessage();
        SyntaxNode? node = root != null && d.Location.IsInSource
            ? root.FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true) : null;

        switch (d.Id)
        {
            case "CS1061":   // 'T' does not contain a definition for 'X'
            case "CS0117":
            {
                if (message.StartsWith("'object' ", StringComparison.Ordinal) || message.StartsWith("'object?' ", StringComparison.Ordinal))
                    return "The value is typed `object`, so the compiler cannot see its members. Read them when the script runs: Dyn(x).Member, Dyn(x).Method(args), foreach (var item in Dyn(x)).";
                if (node == null || model == null) return null;
                var receiver = ReceiverType(node, model);
                return receiver == null ? null : DescribeMembers(receiver, NameOf(node), staticOnly: d.Id == "CS0117");
            }

            case "CS1501":   // No overload for method 'X' takes N arguments
            case "CS7036":   // There is no argument given that corresponds to the required parameter
            case "CS1503":   // Argument N: cannot convert from 'A' to 'B'
            case "CS1739":   // The best overload does not have a parameter named 'X'
            case "CS1729":   // 'T' does not contain a constructor that takes N arguments
            {
                if (node == null || model == null) return null;
                var signatures = CandidateSignatures(node, model);
                return signatures.Count == 0 ? null
                    : (signatures.Count == 1 ? "It is: " : "Its overloads: ") + string.Join(" | ", signatures);
            }

            case "CS0246":   // The type or namespace name 'X' could not be found
            case "CS0103":   // The name 'X' does not exist in the current context
            {
                if (node == null || types == null) return null;
                string name = NameOf(node);
                if (name.Length == 0) return null;
                if (d.Id == "CS0103" && !char.IsUpper(name[0]))
                    return $"No variable `{name}` exists in this session: a variable only exists if the script that declared it compiled. Declare it in this script (see the variables line of your last successful result).";
                var namespaces = types.NamespacesOf(name);
                if (namespaces.Count > 1)
                    return $"`{name}` exists in several namespaces: {string.Join(", ", namespaces.Take(5))}. Write the one you mean in full, or add its `using`.";
                if (namespaces.Count == 1) return $"`{name}` is in {namespaces[0]}: add `using {namespaces[0]};` at the top.";
                var similar = types.Similar(name);
                return similar.Count == 0 ? null
                    : "No such type. Closest: " + string.Join(", ", similar.Select(s => $"{s.Name} ({s.Namespace})"));
            }

            case "CS0234":   // The type or namespace name 'X' does not exist in the namespace 'N'
            {
                if (node == null || types == null) return null;
                string name = NameOf(node);
                var namespaces = types.NamespacesOf(name);
                if (namespaces.Count > 0) return $"`{name}` is in: {string.Join(", ", namespaces.Take(5))}.";
                var similar = types.Similar(name);
                return similar.Count == 0 ? null
                    : "Closest: " + string.Join(", ", similar.Select(s => $"{s.Namespace}.{s.Name}"));
            }

            case "CS0122":   // 'X' is inaccessible due to its protection level
                return "That type or member is not public, so a script cannot name it. Reach it through the value instead: Dyn(obj).Member, Dyn(obj).Method(args); for a callback parameter write (Action<dynamic>)(x => …).";

            case "CS1977":   // Cannot use a lambda expression as an argument to a dynamically dispatched operation
                return "A lambda passed to a dynamic call needs its type written: (Action<dynamic>)(x => …) or (Func<dynamic, bool>)(x => …).";

            case "CS1973":   // extension method cannot be dynamically dispatched
                return "LINQ and other extension methods cannot be called on a dynamic value. Give it a type first: ((IEnumerable<object>)value).Where(…), or foreach over it.";

            case "CS4033":   // The 'await' operator can only be used within an async method
            case "CS4034":
                return "`await` inside a lambda needs the lambda to be async: async x => await …";

            case "CS1529":   // A using clause must precede all other elements
                return "Put `using` lines at the very top of the script, before any statement.";
        }
        return null;
    }

    private static string NameOf(SyntaxNode node) => node switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    private static ITypeSymbol? ReceiverType(SyntaxNode memberName, SemanticModel model)
    {
        foreach (var node in memberName.AncestorsAndSelf())
        {
            switch (node)
            {
                case MemberAccessExpressionSyntax access when access == memberName || access.Name.Span.Contains(memberName.Span):
                    return model.GetTypeInfo(access.Expression).Type
                        ?? model.GetSymbolInfo(access.Expression).Symbol as ITypeSymbol;
                case MemberBindingExpressionSyntax binding when binding == memberName || binding.Name.Span.Contains(memberName.Span):
                    var conditional = binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>()
                        .FirstOrDefault(c => c.WhenNotNull.Span.Contains(binding.Span));
                    return conditional == null ? null : model.GetTypeInfo(conditional.Expression).Type;
                case StatementSyntax:
                    return null;
            }
        }
        return null;
    }

    /// <summary>The real members closest to a guessed name, with signatures; or the type's members when nothing is close.</summary>
    private static string? DescribeMembers(ITypeSymbol type, string guessed, bool staticOnly)
    {
        if (type.TypeKind == TypeKind.Error) return null;
        if (type is INamedTypeSymbol { IsGenericType: true } named && IsTask(named))
            return $"That is a Task: `await` it first, then read `.{guessed}` from the result.";

        var all = new List<ISymbol>();
        for (ITypeSymbol? t = type; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
            all.AddRange(t.GetMembers());
        if (type.TypeKind == TypeKind.Interface)
            foreach (var parent in type.AllInterfaces) all.AddRange(parent.GetMembers());

        var usable = all.Where(m => !m.IsImplicitlyDeclared && m.CanBeReferencedByName
                && m is IPropertySymbol or IFieldSymbol or IEventSymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary })
            .Where(m => !staticOnly || m.IsStatic)
            .ToList();
        var visible = usable.Where(m => m.DeclaredAccessibility == Accessibility.Public).ToList();
        string typeName = type.ToDisplayString(TypeFormat);

        var close = KliveAgentNames.Closest(guessed, visible.Select(m => m.Name), 4);
        if (close.Count > 0)
            return $"{typeName} has: " + string.Join(" | ",
                visible.Where(m => close.Contains(m.Name)).Select(Describe).Distinct().Take(6));

        var hidden = KliveAgentNames.Closest(guessed, usable.Where(m => m.DeclaredAccessibility != Accessibility.Public).Select(m => m.Name), 3);
        if (hidden.Count > 0)
            return $"{typeName} has no public `{guessed}`, but it has non-public: {string.Join(", ", hidden)}. Reach those with Dyn(obj).{hidden[0]}.";

        var names = visible.Select(m => m.Name).Distinct().Take(18).ToList();
        return names.Count == 0 ? $"{typeName} has no public members a script can use; try Dyn(obj) to see the rest."
            : $"{typeName} has no `{guessed}`. Its public members: {string.Join(", ", names)}{(visible.Select(m => m.Name).Distinct().Count() > names.Count ? ", …" : "")}";
    }

    private static bool IsTask(INamedTypeSymbol type) =>
        type.Name is "Task" or "ValueTask" && type.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks";

    private static string Describe(ISymbol member) => member switch
    {
        IMethodSymbol method => $"{method.Name}({string.Join(", ", method.Parameters.Select(p => $"{p.Type.ToDisplayString(TypeFormat)} {p.Name}"))}) -> {method.ReturnType.ToDisplayString(TypeFormat)}",
        IPropertySymbol property => $"{property.Name} : {property.Type.ToDisplayString(TypeFormat)}",
        IFieldSymbol field => $"{field.Name} : {field.Type.ToDisplayString(TypeFormat)}",
        _ => member.Name,
    };

    /// <summary>The signatures a call could have meant: every overload of the method (or constructor) being called.</summary>
    private static List<string> CandidateSignatures(SyntaxNode node, SemanticModel model)
    {
        var symbols = new List<ISymbol>();
        foreach (var ancestor in node.AncestorsAndSelf())
        {
            if (ancestor is InvocationExpressionSyntax invocation)
            {
                symbols.AddRange(model.GetMemberGroup(invocation.Expression));
                if (symbols.Count == 0) symbols.AddRange(model.GetSymbolInfo(invocation).CandidateSymbols);
                break;
            }
            if (ancestor is BaseObjectCreationExpressionSyntax creation)
            {
                var info = model.GetSymbolInfo(creation);
                symbols.AddRange(info.CandidateSymbols);
                if (symbols.Count == 0 && model.GetTypeInfo(creation).Type is INamedTypeSymbol created)
                    symbols.AddRange(created.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public));
                break;
            }
            if (ancestor is StatementSyntax) break;
        }
        return symbols.OfType<IMethodSymbol>()
            .Select(m => m.MethodKind == MethodKind.Constructor
                ? $"new {m.ContainingType.Name}({string.Join(", ", m.Parameters.Select(p => $"{p.Type.ToDisplayString(TypeFormat)} {p.Name}"))})"
                : Describe(m))
            .Distinct().Take(5).ToList();
    }

    // ── runtime ──

    private static readonly Regex SubmissionLine = new(@"Submission#\d+.*:line (\d+)", RegexOptions.Compiled);

    /// <summary>The 1-based script line an exception was thrown from, when the trace carries one.</summary>
    internal static int? ScriptLineOf(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (string.IsNullOrEmpty(e.StackTrace)) continue;
            foreach (var frame in e.StackTrace.Split('\n'))
            {
                var match = SubmissionLine.Match(frame);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int line) && line > 0) return line;
            }
        }
        return null;
    }

    /// <summary>
    /// Renders a runtime exception: type and message, the script line it came from, the inner-exception
    /// chain and the top stack frames. Reflection wraps the real error in TargetInvocationException, so
    /// its inner exception is shown as the primary one.
    /// </summary>
    internal static string FormatRuntimeError(Exception ex, string? code)
    {
        var primary = ex is System.Reflection.TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
        var sb = new StringBuilder();
        sb.AppendLine($"Runtime error: {primary.GetType().Name}: {primary.Message}");

        if (ScriptLineOf(ex) is { } line && !string.IsNullOrEmpty(code))
        {
            var lines = code.Replace("\r\n", "\n").Split('\n');
            if (line <= lines.Length) sb.AppendLine($"  at line {line}: {lines[line - 1].Trim()}");
        }
        if (HintFor(primary) is { } hint) sb.AppendLine("  " + hint);

        var inner = primary.InnerException;
        for (int depth = 0; inner != null && depth < 5; depth++)
        {
            sb.AppendLine($"  → caused by {inner.GetType().Name}: {inner.Message}");
            inner = inner.InnerException;
        }

        if (!string.IsNullOrWhiteSpace(primary.StackTrace))
        {
            var frames = primary.StackTrace.Replace("\r\n", "\n").Split('\n')
                .Select(f => f.Trim())
                .Where(f => f.Length > 0)
                .Take(6)
                .ToList();
            if (frames.Count > 0)
            {
                sb.AppendLine("Stack:");
                foreach (var f in frames) sb.AppendLine($"  {f}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string? HintFor(Exception ex)
    {
        string message = ex.Message ?? string.Empty;
        switch (ex)
        {
            case NullReferenceException:
                return "Something on that line was null. The usual cause is a reflection lookup that found nothing (GetProperty/GetField/GetMethod return null for a wrong name or a non-public member) or a call that returned null. Dyn(obj).Name reads a member by name, non-public included, and names the closest real ones when it is wrong.";
            case System.Reflection.TargetParameterCountException:
            case System.Reflection.AmbiguousMatchException:
                return "A reflection call had the wrong arguments or matched several overloads. Dyn(obj).Method(args) picks the overload from the arguments you pass.";
            case System.Reflection.TargetException when message.Contains("does not match target type", StringComparison.OrdinalIgnoreCase):
                return "A Dyn(...) value was handed to reflection. Pass Raw(x) to get the object itself, or keep using Dyn(x).Member.";
        }
        if (ex.GetType().Name == "RuntimeBinderException")
        {
            if (message.Contains("inaccessible due to its protection level", StringComparison.Ordinal)
                || message.Contains("does not contain a definition", StringComparison.Ordinal))
                return "That value's type or member is not public, so plain dynamic access cannot see it. Wrap the value: Dyn(x).Member.";
            if (message.Contains("null reference", StringComparison.OrdinalIgnoreCase))
                return "The value was null when its member was read: the call that produced it returned null.";
        }
        return null;
    }
}
