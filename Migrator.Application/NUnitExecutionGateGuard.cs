using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Application;

/// <summary>
/// A source NUnit skip/explicit gate must never disappear and turn a test into
/// an automatically executable target. Until source-to-target attribute
/// provenance is implemented, conservatively refuse the whole source file.
/// This is a syntax guard, not a complete NUnit semantic attribute resolver.
/// </summary>
internal static class NUnitExecutionGateGuard
{
    internal sealed record Gate(int SourceLine, string GateName, string Scope);

    public static IReadOnlyList<Gate> Find(SyntaxNode root)
    {
        var findings = new List<Gate>();
        foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var tests = type.Members.OfType<MethodDeclarationSyntax>()
                .Where(method => method.AttributeLists
                    .SelectMany(list => list.Attributes)
                    .Any(attribute => Name(attribute) is
                        "Test" or "TestCase" or "TestCaseSource"))
                .ToArray();
            var explicitFixture = type.AttributeLists.SelectMany(list => list.Attributes)
                .Any(attribute => Name(attribute) == "TestFixture");
            if (tests.Length == 0 && !explicitFixture)
                continue;

            Inspect(type.AttributeLists, type.Identifier.ValueText, findings);
            foreach (var test in tests)
                Inspect(test.AttributeLists,
                    type.Identifier.ValueText + "." + test.Identifier.ValueText,
                    findings);
        }
        return findings;
    }

    private static void Inspect(IEnumerable<AttributeListSyntax> lists,
        string scope, List<Gate> findings)
    {
        foreach (var attr in lists.SelectMany(list => list.Attributes))
        {
            var name = Name(attr);
            var line = attr.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (name is "Ignore" or "Explicit")
                findings.Add(new Gate(line, name, scope));

            // NUnit cases/fixtures can also be gated by named attribute
            // arguments, e.g. [TestCase(1, Ignore = "reason")].
            if (name is not ("Test" or "TestCase" or "TestCaseSource"
                or "TestFixture"))
                continue;
            foreach (var argument in attr.ArgumentList?.Arguments
                ?? default(SeparatedSyntaxList<AttributeArgumentSyntax>))
            {
                var property = argument.NameEquals?.Name.Identifier.ValueText;
                if (property is "Ignore" or "Explicit")
                    findings.Add(new Gate(line, name + "." + property, scope));
            }
        }
    }

    private static string Name(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString().Split('.').Last();
        return name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^"Attribute".Length] : name;
    }
}
