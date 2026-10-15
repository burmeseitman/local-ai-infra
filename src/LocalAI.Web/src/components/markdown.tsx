import { memo } from "react";
import ReactMarkdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";

// Model output is untrusted:
// - raw HTML is never rendered (no rehype-raw), so <script>/<iframe> stay inert text;
// - react-markdown's default URL transform strips javascript:/data: links;
// - images are NOT loaded, because a crafted reply could otherwise make the browser
//   send requests (and data in the URL) to an arbitrary host.
const components: Components = {
  a: ({ href, children }) => (
    <a href={href} target="_blank" rel="noopener noreferrer nofollow">
      {children}
    </a>
  ),
  img: ({ alt }) => <span className="md-image-blocked">[image not loaded{alt ? `: ${alt}` : ""}]</span>,
  table: ({ children }) => (
    <div className="md-table-wrap">
      <table>{children}</table>
    </div>
  ),
};

export const Markdown = memo(function Markdown({ text }: { text: string }) {
  return (
    <div className="markdown">
      <ReactMarkdown remarkPlugins={[remarkGfm]} components={components} skipHtml>
        {text}
      </ReactMarkdown>
    </div>
  );
});
