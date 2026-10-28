using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Migrator.Core;

namespace Migrator.Application;

/// <summary>
/// Read-only, bounded Roslyn inspection of project Page Object locator definitions.
/// Selector extensions must be proven by their in-scope implementation; names alone
/// never authorize a mapping. All generated expressions are PROPOSALS for review.
/// </summary>
public sealed class PomSourceInspector
{
    private const int MaxFiles = 1500;
    private const long MaxSourceBytes = 24L * 1024 * 1024;
    private const int MaxResults = 250;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private sealed record Input(string Relative, byte[] Content, SyntaxNode Root);
    private sealed record HelperDefinition(string Method, string Attribute, string Path, int Line);
    private sealed record RawMember(string Owner, string Member, string Type, string Path, int Line,
        string Expression, IReadOnlyList<LocatorPredicateEvidence> Predicates,
        string? Css, string? XPath, bool Unsupported, bool Contextual);

    public PomSourceInspection Inspect(
        string sourceDirectory,
        IReadOnlyList<string>? pageTypes = null,
        string pageVariable = "page",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            throw new ArgumentException("Source directory is required.", nameof(sourceDirectory));
        var root = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("POM_SOURCE_DIRECTORY_MISSING: " + root);
        if (!System.Text.RegularExpressions.Regex.IsMatch(pageVariable, @"^@?[a-zA-Z_]\w*$"))
            throw new ArgumentException("PAGE_VARIABLE_INVALID: expected a C# identifier.", nameof(pageVariable));

        var paths = new List<string>();
        Collect(root, 0);
        var inputs = new List<Input>();
        long size = 0;
        foreach (var path in paths.OrderBy(p => p, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = File.ReadAllBytes(path);
            size += content.LongLength;
            if (size > MaxSourceBytes)
                throw new InvalidOperationException("POM_SOURCE_TOO_LARGE: limit is 24 MiB of C# sources.");
            string text;
            try
            {
                text = Utf8.GetString(content);
            }
            catch (DecoderFallbackException)
            {
                // A non-UTF8 source is not proof of a selector. Reject instead of
                // interpreting the same bytes differently from the project compiler.
                throw new InvalidOperationException("POM_SOURCE_INVALID_UTF8: inspect sources as UTF-8.");
            }
            var relative = Relative(root, path);
            var tree = CSharpSyntaxTree.ParseText(text, path: relative);
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidOperationException("POM_SOURCE_PARSE_ERROR: " + relative);
            inputs.Add(new Input(relative, content, tree.GetRoot()));
        }
        var treeHash = ContentTreeHasher.ComputeBytes(inputs.Select(x => (x.Relative, x.Content)));
        var allClasses = inputs.SelectMany(input => input.Root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Select(c => (Source: input.Relative, Class: c))).ToArray();

        var helpers = ResolveSelectorHelpers(allClasses);
        var rawMembers = allClasses.SelectMany(c => ExtractMembers(c.Source, c.Class, helpers))
            .ToArray();

        var filters = pageTypes?.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim()).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<PomLocatorEvidence>();
        foreach (var member in rawMembers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filters.Count > 0 && !filters.Contains(member.Owner))
                continue;

            var proposed = member.Css != null
                ? "Page.Locator(" + JsonSerializer.Serialize(member.Css) + ")"
                : member.XPath != null
                    ? "Page.Locator(" + JsonSerializer.Serialize("xpath=" + member.XPath) + ")"
                    : null;
            var status = proposed == null ? "insufficient_evidence" : "requires_review";
            var reason = proposed == null
                ? "Unknown selector builder or non-literal selector. Do not invent a target locator."
                : member.Contextual
                    ? "Selector is scoped to a source context; applying it from Page directly may change behavior."
                    : "Selector is derived from literal source calls and verified helper definitions. DOM context and matching count still need review.";
            selected.Add(new PomLocatorEvidence(
                member.Owner, pageVariable + "." + member.Member, member.Path, member.Line,
                member.Type, Clip(member.Expression), member.Predicates, proposed,
                status, reason));
        }

        // One bounded nested level is useful for Page.Table.Items: the source
        // child selector runs inside the Page.Table source context. A CSS descendant
        // expression is a *candidate*, not a proven Page Object migration.
        foreach (var parent in rawMembers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!parent.Owner.EndsWith("Page", StringComparison.Ordinal)
                || (filters.Count > 0 && !filters.Contains(parent.Owner)))
                continue;
            foreach (var child in rawMembers.Where(m => m.Owner == parent.Type))
            {
                if (parent.Css == null || child.Css == null || parent.Unsupported || child.Unsupported)
                    continue;
                var combined = parent.Css + " " + child.Css;
                selected.Add(new PomLocatorEvidence(
                    parent.Owner, pageVariable + "." + parent.Member + "." + child.Member,
                    child.Path, child.Line, child.Type, Clip(parent.Expression + " -> " + child.Expression),
                    parent.Predicates.Concat(child.Predicates).ToArray(),
                    "Page.Locator(" + JsonSerializer.Serialize(combined) + ")",
                    "requires_review",
                    "Two-level Page Object composition inferred from source member types and context. Validate child/root semantics, DOM and selector uniqueness before using."));
            }
        }

        var selectedPublicMethods = allClasses.SelectMany(c => ExtractHelperMethods(c.Source, c.Class))
            .Where(m => filters.Count == 0 || filters.Contains(m.SourceType)
                || m.SourceType.Equals("LocalStorage", StringComparison.Ordinal))
            .Take(MaxResults).ToArray();
        // Preserve lexical call sequences and reachable private helpers. In particular,
        // a DatePicker method calling its private Blur() cannot be safely treated as
        // a single FillAsync just because it contains one input action.
        var methodEvidence = PomMethodLineage.Expand(allClasses, selectedPublicMethods)
            .Take(MaxResults).ToArray();

        var ordered = selected.OrderBy(m => m.SourceType, StringComparer.Ordinal)
            .ThenBy(m => m.SourceMemberPath, StringComparer.Ordinal)
            .ThenBy(m => m.SourcePath, StringComparer.Ordinal)
            .ToArray();
        return new PomSourceInspection(
            "migrator-pom-evidence/v1alpha2", root, treeHash, inputs.Count,
            ordered.Length, ordered.Take(MaxResults).ToArray(), methodEvidence,
            new[]
            {
                "Every locator is a review-required proposal, never an automatically applied UiTarget mapping.",
                "Selector/helper AST does not establish DOM matching count, auth, wait or browser behavior.",
                "POM call order is lexical syntax, not a dynamic runtime trace through branches or retries.",
                "Same-class private helper calls are followed only to a bounded depth; inherited and dynamic dispatch is unverified.",
                "Source test storage-before-navigation evidence does not establish browser origin or storage lifetime.",
                "No executable POM or LocalStorage mapping is automatically produced; target source must be reviewed.",
                "Only in-scope UTF-8 C# sources are captured; this is not MSBuild or a runtime behavior oracle."
            })
        {
            StorageAndNavigationFlows = PomMethodLineage.StorageFlows(allClasses)
        };

        void Collect(string directory, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 32)
                throw new InvalidOperationException("POM_SCOPE_TOO_DEEP: choose a narrower directory.");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("POM_SYMLINK_REJECTED: directory is a symlink.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("POM_SYMLINK_REJECTED: linked sources are not inspected.");
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    var name = Path.GetFileName(entry);
                    if (name is "bin" or "obj" or ".git" or "node_modules" or "private-generated")
                        continue;
                    Collect(entry, depth + 1);
                }
                else if (entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(entry);
                    if (paths.Count > MaxFiles)
                        throw new InvalidOperationException("POM_SCOPE_TOO_LARGE: limit is 1500 C# source files.");
                }
            }
        }
    }

    private static Dictionary<string, HelperDefinition> ResolveSelectorHelpers(
        IEnumerable<(string Source, ClassDeclarationSyntax Class)> classes)
    {
        var candidates = new Dictionary<string, List<HelperDefinition?>>(StringComparer.Ordinal);
        foreach (var (path, type) in classes.Where(c => c.Class.Identifier.ValueText == "SelectorExtensions"))
        {
            foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
            {
                if (method.ParameterList.Parameters.Count < 2)
                    continue;
                var methodName = method.Identifier.ValueText;
                var argName = method.ParameterList.Parameters.Last().Identifier.ValueText;
                var attribute = (string?)null;
                foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (call.Expression is not MemberAccessExpressionSyntax member
                        || call.ArgumentList.Arguments.Count == 0)
                        continue;
                    if (member.Name.Identifier.ValueText == "WithAttribute"
                        && call.ArgumentList.Arguments.Count >= 2
                        && Literal(call.ArgumentList.Arguments[0].Expression) is string attr
                        && call.ArgumentList.Arguments[1].Expression is IdentifierNameSyntax value
                        && value.Identifier.ValueText == argName)
                        attribute = attr;
                    else if (member.Name.Identifier.ValueText == "WithId"
                        && call.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax identifier
                        && identifier.Identifier.ValueText == argName)
                        attribute = "id";
                }
                if (!candidates.TryGetValue(methodName, out var group))
                    candidates[methodName] = group = new List<HelperDefinition?>();
                group.Add(attribute == null ? null : new HelperDefinition(methodName,
                    attribute, path, Line(method)));
            }
        }
        // Every overload must have a consistent attribute. Conflicts are not
        // silently resolved by picking the first extension implementation.
        return candidates.Where(g => g.Value.Count > 0 && g.Value.All(x => x != null)
                        && g.Value.Select(x => x!.Attribute).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(g => g.Key, g => g.Value[0]!, StringComparer.Ordinal);
    }

    private static IEnumerable<RawMember> ExtractMembers(string path, ClassDeclarationSyntax type,
        IReadOnlyDictionary<string, HelperDefinition> helpers)
    {
        var properties = type.Members.OfType<PropertyDeclarationSyntax>()
            .ToDictionary(p => p.Identifier.ValueText, p => p.Type.ToString(), StringComparer.Ordinal);
        foreach (var statement in type.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var memberName = statement.Left switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                MemberAccessExpressionSyntax access when access.Expression is ThisExpressionSyntax
                    => access.Name.Identifier.ValueText,
                _ => string.Empty
            };
            if (!properties.TryGetValue(memberName, out var memberType))
                continue;
            if (!statement.Right.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .Any(i => Method(i) is "CreateControl" or "CreateElementsCollection"))
                continue;

            var selectors = new List<LocatorPredicateEvidence>();
            var fragments = new List<string>();
            var hasUnsupported = false;
            string? xpath = null;
            foreach (var call in statement.Right.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                         .OrderBy(i => i.Span.End).ThenBy(i => i.Span.Length))
            {
                var name = Method(call);
                if (name == null)
                    continue;
                var args = call.ArgumentList.Arguments;
                if (helpers.TryGetValue(name, out var helper))
                {
                    if (args.Count != 1 || Literal(args[0].Expression) is not string literal)
                    {
                        hasUnsupported = true;
                        continue;
                    }
                    selectors.Add(new LocatorPredicateEvidence(name, helper.Attribute,
                        literal, helper.Path, helper.Line));
                    fragments.Add("[" + helper.Attribute + "=\"" + EscapeCss(literal) + "\"]");
                }
                else if (name is "XPath" or "CssSelector" or "Id" or "Name" or "ClassName"
                    && call.Expression is MemberAccessExpressionSyntax ma
                    && ma.Expression.ToString() is "By" or "OpenQA.Selenium.By")
                {
                    if (args.Count != 1 || Literal(args[0].Expression) is not string value)
                    {
                        hasUnsupported = true;
                        continue;
                    }
                    if (name == "XPath")
                        xpath = value;
                    else
                    {
                        var attr = name switch
                        {
                            "Id" => "id",
                            "Name" => "name",
                            "ClassName" => "class",
                            _ => string.Empty
                        };
                        if (name == "CssSelector")
                            fragments.Add(value);
                        else
                            fragments.Add("[" + attr + "=\"" + EscapeCss(value) + "\"]");
                    }
                }
                else if (name.StartsWith("With", StringComparison.Ordinal))
                {
                    hasUnsupported = true;
                }
            }

            var memberTypeName = memberType.Split('<')[0].Split('.').Last().Trim();
            var fromContext = !statement.Right.ToString().Contains("webDriver.Search(", StringComparison.Ordinal)
                && !statement.Right.ToString().Contains("WrappedDriver.Search(", StringComparison.Ordinal);
            var css = !hasUnsupported && xpath == null && fragments.Count > 0
                ? string.Join(" ", fragments)
                : null;
            yield return new RawMember(type.Identifier.ValueText, memberName, memberTypeName,
                path, Line(statement), statement.Right.ToString(),
                selectors, css, !hasUnsupported ? xpath : null, hasUnsupported, fromContext);
        }
    }

    private static IEnumerable<SourceHelperEvidence> ExtractHelperMethods(string path, ClassDeclarationSyntax type)
    {
        var className = type.Identifier.ValueText;
        var isStorage = className.Equals("LocalStorage", StringComparison.Ordinal);
        var isControl = className.EndsWith("Page", StringComparison.Ordinal)
            || className is "TableItem" or "ScCombobox" or "ScComboBox" or "Loader" or "DatePicker"
            || isStorage;
        if (!isControl)
            yield break;

        foreach (var method in type.Members.OfType<MethodDeclarationSyntax>()
                     .Where(m => m.Modifiers.Any(x => x.IsKind(SyntaxKind.PublicKeyword))))
        {
            var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(Method).Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!).Distinct(StringComparer.Ordinal).Take(30).ToArray();
            if (calls.Length == 0 && !isStorage)
                continue;
            var source = method.ToString();
            var keys = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => Method(i) == "SetItem" && i.ArgumentList.Arguments.Count >= 1)
                .Select(i => Literal(i.ArgumentList.Arguments[0].Expression))
                .OfType<string>().Distinct(StringComparer.Ordinal).Take(16).ToArray();
            var classification = isStorage
                ? source.Contains("localStorage.", StringComparison.Ordinal)
                    ? "javascript-local-storage" : "composite-storage-helper"
                : "project-control-method";
            yield return new SourceHelperEvidence(
                className, method.Identifier.ValueText, path, Line(method),
                calls, keys, classification,
                isStorage
                    ? "Verify browser origin, call timing, keys, exact string/JSON serialization and state lifetime before mapping."
                    : "Inspect the full source method, control context, waits, and side effects before mapping to Playwright.");
        }
    }

    private static string? Method(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => null
    };

    private static string? Literal(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText : null;

    private static string EscapeCss(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\d ", StringComparison.Ordinal)
            .Replace("\n", "\\a ", StringComparison.Ordinal);

    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Clip(string value) =>
        value.Length <= 650 ? value : value[..650] + "…";
}