using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Roslyn;

/// <summary>
/// Closed, side-effect-free NUnit discovery subset. It never evaluates a C# data source.
/// Only literal rows in a local static readonly array or expression-bodied static
/// property can be lowered to equivalent NUnit TestCase attributes. Everything else
/// is blocked before a runnable candidate can be emitted.
/// </summary>
public static class NUnitExecutionSemanticsInspector
{
    public sealed record Issue(int Line, string Code, string Explanation);
    public sealed record Evidence(int Line, string Kind, string SourceSha256,
        string TargetExpression, bool RequiresReview, string? Reason = null);
    public sealed record Case(int Line, string Method, string TargetExpression,
        string RunState, string? Reason);
    public sealed record Review(IReadOnlyList<Issue> Issues,
        IReadOnlyList<Evidence> Evidence, IReadOnlyList<Case> Cases,
        IReadOnlyDictionary<int, IReadOnlyList<string>> ExpandedSources)
    {
        public bool Safe => Issues.Count == 0;
    }

    public static Review Inspect(SyntaxNode root)
    {
        var issues = new List<Issue>();
        var evidence = new List<Evidence>();
        var cases = new List<Case>();
        var expanded = new Dictionary<int, IReadOnlyList<string>>();
        var hasNUnitUsing = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Any(u => u.Alias == null && u.Name?.ToString() == "NUnit.Framework");
        var localNames = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Select(t => t.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var tests = type.Members.OfType<MethodDeclarationSyntax>()
                .Where(m => Attributes(m.AttributeLists).Any(a =>
                    Name(a) is "Test" or "TestCase" or "TestCaseSource")).ToArray();
            if (tests.Length == 0)
                continue;

            // NUnit's fixture-level run state is inherited. A generated fixture
            // cannot silently discard a base's execution gates. Without a complete
            // inheritance proof, leave the original fixture blocked.
            if (type.BaseList?.Types.Count > 0
                && !type.BaseList.Types.All(t =>
                    t.Type.ToString() is "PageTest" or "Microsoft.Playwright.NUnit.PageTest"))
            {
                var baseTypes = type.BaseList.Types.Select(t => t.Type.ToString()).ToArray();
                if (baseTypes.Any(b => localNames.Contains(b.Split('.').Last())
                    && root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                        .Any(other => other.Identifier.ValueText == b.Split('.').Last()
                            && Attributes(other.AttributeLists).Any(a =>
                                Name(a) is "Ignore" or "Explicit"))))
                    AddIssue(type, "NUNIT_INHERITED_GATE_REQUIRES_REVIEW",
                        "An inherited NUnit Ignore/Explicit fixture gate cannot be dropped.", issues);
                // All other inheritance is verified by the existing lifecycle
                // and target assembly planners, not guessed by this inspector.
            }

            var fixtureGate = "runnable";
            string? fixtureReason = null;
            foreach (var attribute in Attributes(type.AttributeLists))
            {
                var name = Name(attribute);
                if (name is "Ignore" or "Explicit")
                {
                    var gate = Gate(attribute, name, hasNUnitUsing, issues, evidence);
                    if (gate != null)
                    {
                        fixtureGate = name == "Ignore" ? "ignored" : "explicit";
                        fixtureReason = gate;
                    }
                }
                else if (name == "TestFixture")
                {
                    foreach (var arg in attribute.ArgumentList?.Arguments
                        ?? default(SeparatedSyntaxList<AttributeArgumentSyntax>))
                    {
                        if (arg.NameEquals?.Name.Identifier.ValueText is "Ignore" or "Explicit")
                            AddIssue(attribute, "NUNIT_FIXTURE_NAMED_GATE_REQUIRES_REVIEW",
                                "A TestFixture named execution gate needs exact NUnit fixture-overload evidence.", issues);
                    }
                }
            }

            foreach (var method in tests)
            {
                var methodGate = fixtureGate;
                var methodReason = fixtureReason;
                var parameterTypes = method.ParameterList.Parameters
                    .Select(p => p.Type?.ToString() ?? "").ToArray();
                var sourceAttributes = Attributes(method.AttributeLists).ToArray();
                foreach (var attr in sourceAttributes.Where(a => Name(a) is "Ignore" or "Explicit"))
                {
                    var name = Name(attr);
                    var gate = Gate(attr, name, hasNUnitUsing, issues, evidence);
                    if (gate != null)
                    {
                        methodGate = name == "Ignore" ? "ignored" : "explicit";
                        methodReason = gate;
                    }
                }
                var parameterized = sourceAttributes.Any(a =>
                    Name(a) is "TestCase" or "TestCaseSource");
                foreach (var attribute in sourceAttributes)
                {
                    var name = Name(attribute);
                    if (name == "TestCase")
                    {
                        if (!KnownNUnit(attribute, hasNUnitUsing))
                        {
                            AddIssue(attribute, "NUNIT_ATTRIBUTE_BINDING_AMBIGUOUS",
                                "TestCase attribute is not unambiguously NUnit.Framework.", issues);
                            continue;
                        }
                        var named = NamedCaseGate(attribute, issues);
                        var state = named.State ?? methodGate;
                        var reason = named.Reason ?? methodReason;
                        var target = attribute.ToString();
                        evidence.Add(Evid(attribute, "TestCase", target));
                        cases.Add(new Case(Line(attribute), method.Identifier.ValueText,
                            target, state, reason));
                    }
                    else if (name == "TestCaseSource")
                    {
                        if (!KnownNUnit(attribute, hasNUnitUsing))
                        {
                            AddIssue(attribute, "NUNIT_ATTRIBUTE_BINDING_AMBIGUOUS",
                                "TestCaseSource must bind to NUnit.Framework.", issues);
                            continue;
                        }
                        if (!TryExpandSource(type, method, attribute, parameterTypes,
                            out var targets, out var sourceMember, out var reason))
                        {
                            AddIssue(attribute, "NUNIT_TEST_CASE_SOURCE_UNSAFE", reason, issues);
                            evidence.Add(Evid(attribute, "TestCaseSource", "", true, reason));
                            continue;
                        }
                        expanded[attribute.SpanStart] = targets;
                        evidence.Add(Evid(attribute, "TestCaseSource",
                            string.Join(" | ", targets)));
                        evidence.Add(Evid(sourceMember!, "StaticCaseSource",
                            string.Join(" | ", targets)));
                        foreach (var target in targets)
                            cases.Add(new Case(Line(attribute), method.Identifier.ValueText,
                                target, methodGate, methodReason));
                    }
                }
                if (!parameterized)
                    cases.Add(new Case(Line(method), method.Identifier.ValueText,
                        "[Test]", methodGate, methodReason));
            }
        }
        return new Review(issues, evidence, cases, expanded);
    }

    private static (string? State, string? Reason) NamedCaseGate(
        AttributeSyntax attribute, List<Issue> issues)
    {
        string? state = null, reason = null;
        foreach (var arg in attribute.ArgumentList?.Arguments
            ?? default(SeparatedSyntaxList<AttributeArgumentSyntax>))
        {
            var key = arg.NameEquals?.Name.Identifier.ValueText;
            if (key == "Ignore")
            {
                if (!StringLiteral(arg.Expression, out reason))
                    AddIssue(arg, "NUNIT_CASE_GATE_DYNAMIC",
                        "TestCase Ignore reason must be a constant string literal.", issues);
                else
                    state = "ignored";
            }
            else if (key == "Explicit")
            {
                if (!BooleanLiteral(arg.Expression, out var enabled))
                    AddIssue(arg, "NUNIT_CASE_GATE_DYNAMIC",
                        "TestCase Explicit must be a literal bool.", issues);
                else if (enabled && state != "ignored")
                    state = "explicit";
            }
        }
        return (state, reason);
    }

    private static string? Gate(AttributeSyntax attribute, string kind,
        bool hasNUnitUsing, List<Issue> issues, List<Evidence> evidence)
    {
        if (!KnownNUnit(attribute, hasNUnitUsing))
        {
            AddIssue(attribute, "NUNIT_ATTRIBUTE_BINDING_AMBIGUOUS",
                "Ignore/Explicit attribute must unambiguously refer to NUnit.Framework.", issues);
            return null;
        }
        var arguments = attribute.ArgumentList?.Arguments
            ?? default(SeparatedSyntaxList<AttributeArgumentSyntax>);
        var valid = arguments.Count == 0 && kind == "Explicit";
        string? reason = null;
        if (arguments.Count == 1 && arguments[0].NameEquals == null
            && arguments[0].NameColon == null
            && StringLiteral(arguments[0].Expression, out var value))
        {
            reason = value;
            valid = true;
        }
        if (!valid)
        {
            AddIssue(attribute, "NUNIT_EXECUTION_GATE_REQUIRES_REVIEW",
                "NUnit gate has an unsupported or dynamically computed reason.", issues);
            evidence.Add(Evid(attribute, kind, "", true,
                "Unsupported NUnit gate arguments."));
            return null;
        }
        evidence.Add(Evid(attribute, kind, attribute.ToString()));
        return reason ?? "";
    }

    private static bool TryExpandSource(ClassDeclarationSyntax fixture,
        MethodDeclarationSyntax method, AttributeSyntax attribute,
        string[] parameters, out IReadOnlyList<string> targets,
        out MemberDeclarationSyntax? source, out string problem)
    {
        targets = Array.Empty<string>();
        source = null;
        problem = "Only a local static readonly literal array or a static expression-bodied property is supported.";
        var args = attribute.ArgumentList?.Arguments;
        if (args == null || args.Value.Count != 1
            || args.Value[0].NameEquals != null || args.Value[0].NameColon != null)
        {
            problem = "TestCaseSource must contain exactly one nameof(localMember) or string literal.";
            return false;
        }
        var expression = args.Value[0].Expression;
        string? name = null;
        if (StringLiteral(expression, out var literal))
            name = literal;
        else if (expression is InvocationExpressionSyntax invocation
            && invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }
            && invocation.ArgumentList.Arguments.Count == 1
            && invocation.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax identifier)
            name = identifier.Identifier.ValueText;
        if (string.IsNullOrWhiteSpace(name)
            || !SyntaxFacts.IsValidIdentifier(name))
        {
            problem = "External, inherited, parameterized, or computed TestCaseSource references require review.";
            return false;
        }

        ExpressionSyntax? initializer = null;
        var fields = fixture.Members.OfType<FieldDeclarationSyntax>()
            .Where(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == name))
            .ToArray();
        var properties = fixture.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Identifier.ValueText == name).ToArray();
        if (fields.Length + properties.Length != 1)
        {
            problem = "TestCaseSource member must be uniquely declared in the same fixture; inherited/external sources are blocked.";
            return false;
        }
        if (fields.Length == 1)
        {
            var field = fields[0];
            source = field;
            if (!field.Modifiers.Any(SyntaxKind.StaticKeyword)
                || !field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)
                || field.Declaration.Variables.Count != 1)
            {
                problem = "TestCaseSource field must be static readonly with exactly one declarator.";
                return false;
            }
            initializer = field.Declaration.Variables[0].Initializer?.Value;
        }
        else
        {
            var property = properties[0];
            source = property;
            if (!property.Modifiers.Any(SyntaxKind.StaticKeyword)
                || property.ExpressionBody == null || property.AccessorList != null)
            {
                problem = "TestCaseSource property must be static and expression-bodied with no accessor code.";
                return false;
            }
            initializer = property.ExpressionBody.Expression;
        }
        var rows = initializer switch
        {
            ArrayCreationExpressionSyntax { Initializer: { } items } => items.Expressions,
            ImplicitArrayCreationExpressionSyntax { Initializer: { } items } => items.Expressions,
            _ => default
        };
        if (initializer == null || rows.Count == 0)
        {
            problem = "Empty, dynamic, and computed TestCaseSource data are blocked.";
            return false;
        }
        var result = new List<string>();
        foreach (var row in rows)
        {
            SeparatedSyntaxList<ExpressionSyntax> values;
            if (row is ArrayCreationExpressionSyntax { Initializer: { } a })
                values = a.Expressions;
            else if (row is ImplicitArrayCreationExpressionSyntax { Initializer: { } b })
                values = b.Expressions;
            else if (row is ObjectCreationExpressionSyntax { ArgumentList: { } ctor }
                && row is ObjectCreationExpressionSyntax obj
                && obj.Type.ToString().Split('.').Last() == "TestCaseData"
                && obj.Initializer == null)
                values = ctor.Arguments.Select(a => a.Expression)
                    .Aggregate(new SeparatedSyntaxList<ExpressionSyntax>(),
                        (list, expression) => list.Add(expression));
            else
            {
                problem = "Each row must be a literal object[] or new TestCaseData(literal arguments), with no calls or side effects.";
                return false;
            }
            if (values.Count != parameters.Length)
            {
                problem = "TestCaseSource row arity does not match the NUnit test method parameters.";
                return false;
            }
            for (var i = 0; i < values.Count; i++)
            {
                if (!SafeLiteral(values[i], parameters[i]))
                {
                    problem = "TestCaseSource row contains a nonliteral or incompatible parameter at index "
                        + i + "; no source code was executed.";
                    return false;
                }
            }
            result.Add("TestCase(" + string.Join(", ", values.Select(v => v.ToString())) + ")");
        }
        targets = result;
        problem = "";
        return true;
    }

    private static bool SafeLiteral(ExpressionSyntax expression, string parameter)
    {
        var kind = parameter.Replace("global::", "", StringComparison.Ordinal);
        if (kind.StartsWith("System.", StringComparison.Ordinal))
            kind = kind[7..];
        var value = expression switch
        {
            LiteralExpressionSyntax literal => literal.Token.Value,
            PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.UnaryMinusExpression)
                && unary.Operand is LiteralExpressionSyntax literal
                && literal.Token.Value is int => -((int)literal.Token.Value!),
            _ => null
        };
        if (expression.IsKind(SyntaxKind.NullLiteralExpression))
            return kind is "string" or "String" or "object" or "Object";
        if (value is null)
            return false;
        return kind switch
        {
            "string" or "String" => value is string,
            "bool" or "Boolean" => value is bool,
            "int" or "Int32" => value is int,
            "long" or "Int64" => value is int or long,
            "double" or "Double" => value is int or long or double or float,
            "float" or "Single" => value is float or int,
            "decimal" or "Decimal" => value is decimal or int,
            "object" or "Object" => value is string or bool or int or long or double or float or decimal,
            _ => false
        };
    }

    private static bool KnownNUnit(AttributeSyntax a, bool hasNUnitUsing)
    {
        var name = a.Name.ToString().Replace("global::", "", StringComparison.Ordinal);
        if (name.StartsWith("NUnit.Framework.", StringComparison.Ordinal))
            return true;
        return !name.Contains('.') && hasNUnitUsing;
    }
    private static bool StringLiteral(ExpressionSyntax expression, out string? value)
    {
        if (expression is LiteralExpressionSyntax lit
            && lit.IsKind(SyntaxKind.StringLiteralExpression))
        { value = lit.Token.ValueText; return true; }
        value = null;
        return false;
    }
    private static bool BooleanLiteral(ExpressionSyntax expression, out bool value)
    {
        if (expression.IsKind(SyntaxKind.TrueLiteralExpression))
        { value = true; return true; }
        if (expression.IsKind(SyntaxKind.FalseLiteralExpression))
        { value = false; return true; }
        value = false;
        return false;
    }
    private static IEnumerable<AttributeSyntax> Attributes(
        SyntaxList<AttributeListSyntax> lists) =>
        lists.Where(l => l.Target == null).SelectMany(l => l.Attributes);
    private static string Name(AttributeSyntax a)
    {
        var name = a.Name.ToString().Split('.').Last();
        return name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^9] : name;
    }
    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    private static Evidence Evid(SyntaxNode node, string kind,
        string target, bool review = false, string? reason = null) =>
        new(Line(node), kind,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToFullString())))
                .ToLowerInvariant(),
            target, review, reason);
    private static void AddIssue(SyntaxNode node, string code, string message,
        List<Issue> issues) => issues.Add(new Issue(Line(node), code, message));
}
