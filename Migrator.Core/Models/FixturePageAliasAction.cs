namespace Migrator.Core.Models;

/// <summary>
/// Syntax-grounded assignment from a local source Page Object to a fixture
/// Page Object field, e.g. `page = pagef` inside NUnit SetUp.
/// This is a source-object identity relationship, NOT a Playwright IPage field.
/// The adapter may record an explicit target navigation precedent, while the
/// renderer still requires an active target-side navigation before exposing
/// any mapped locator action through this field.
/// </summary>
public sealed class FixturePageAliasAction : TestAction
{
    public string SourceField { get; }
    public string SourceLocal { get; }
    public string SourceText { get; }
    public bool HasExplicitNavigationMapping { get; }

    public FixturePageAliasAction(
        int sourceLine,
        string sourceField,
        string sourceLocal,
        string sourceText,
        bool hasExplicitNavigationMapping = false)
        : base(sourceLine, RecognitionConfidence.SyntaxFallback)
    {
        SourceField = sourceField;
        SourceLocal = sourceLocal;
        SourceText = sourceText;
        HasExplicitNavigationMapping = hasExplicitNavigationMapping;
    }
}
