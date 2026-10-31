namespace Migrator.Roslyn;

public static partial class SemanticPomActionProjector
{
    /// <summary>
    /// A deliberately narrow one-shot DOM read for a reviewed Selenium
    /// IWebElement.Text assertion. Browser expectations stay off: no
    /// auto-wait and no retries. This is NOT the complete WebDriver
    /// rendered-text atom; all templates remain requires_review.
    /// </summary>
    private const string SourceRenderedTextSnapshotJavaScript =
        """
        els => {
          if (els.length === 0) throw new Error('POM_SOURCE_ELEMENT_ABSENT');
          const el = els[0];
          if (!(el instanceof HTMLElement))
            throw new Error('POM_TEXT_NON_HTML_ELEMENT_REVIEW');
          for (let ancestor = el; ancestor; ancestor = ancestor.parentElement) {
            const style = getComputedStyle(ancestor);
            if (style.display === 'none' || style.visibility === 'hidden' ||
                style.visibility === 'collapse') return '';
          }
          const nodes = [el, ...el.querySelectorAll('*')];
          for (const item of nodes) {
            const ws = getComputedStyle(item).whiteSpace;
            if (ws === 'pre' || ws === 'pre-wrap' || ws === 'break-spaces')
              throw new Error('POM_TEXT_PRESERVED_WHITESPACE_REVIEW');
          }
          // A narrow observed normal-white-space subset. Do not claim
          // complete Selenium Get Element Text / WebDriver atom parity.
          return el.innerText
            .replace(/\u00a0/g, ' ')
            .replace(/[ \t\f\v]+/g, ' ')
            .replace(/ *\n */g, '\n')
            .trim();
        }
        """;
}
