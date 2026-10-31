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
          let directPreservedWhitespace = false;
          for (const item of nodes) {
            const style = getComputedStyle(item);
            const ws = style.whiteSpace;
            // CSS text-transform changes the WebDriver rendered-text atom.
            // Do not return raw innerText as if this were source equivalent.
            if (style.textTransform !== 'none')
              throw new Error('POM_TEXT_TRANSFORM_REVIEW');
            if (ws === 'break-spaces')
              throw new Error('POM_TEXT_PRESERVED_WHITESPACE_REVIEW');
            if (ws === 'pre' || ws === 'pre-wrap') {
              // Permit only the measured leaf, single-line subset. Mixed
              // child display modes and multiline preformatted text remain
              // review-only rather than guessing a full WebDriver atom.
              if (item !== el || el.childElementCount !== 0)
                throw new Error('POM_TEXT_PRESERVED_WHITESPACE_REVIEW');
              directPreservedWhitespace = true;
            } else if (ws !== 'normal' && ws !== 'nowrap') {
              throw new Error('POM_TEXT_UNSUPPORTED_CSS_WHITESPACE_REVIEW');
            }
          }
          const raw = el.innerText;
          if (directPreservedWhitespace && /[\r\n]/.test(raw))
            throw new Error('POM_TEXT_PRESERVED_MULTILINE_REVIEW');
          // Measured simple Unicode subset: U+00A0 is presented as a
          // normal space; bidi direction markers and U+200B zero-width
          // spaces are not displayed. Keep combining codepoints intact.
          // Other Unicode whitespace is not normalized speculatively.
          if (/[\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]/.test(raw))
            throw new Error('POM_TEXT_UNVERIFIED_UNICODE_WHITESPACE_REVIEW');
          const rendered = raw.replace(/\u00a0/g, ' ')
            .replace(/[\u200b\u200e\u200f]/g, '');
          if (directPreservedWhitespace) return rendered;
          // Normal/nowrap is a bounded reviewed subset; this isn't a
          // full WebDriver getVisibleText algorithm.
          return rendered.replace(/[ \t\f\v]+/g, ' ')
            .replace(/ *\n */g, '\n').trim();
        }
        """;
}
