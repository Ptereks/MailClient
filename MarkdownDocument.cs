using System.IO;
using Markdig;

namespace ZennoVibe.MailClient;

internal static class MarkdownDocument
{
    // Raw HTML in the README is rendered as text: the documentation view is sealed like the
    // message viewer and must not carry anything that could request a resource.
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    public static string LoadReadmeHtml()
    {
        var path = Path.Combine(AppPaths.BaseDirectory, "README.txt");
        if (!File.Exists(path))
            return Wrap("<p>README.txt not found next to MailClient.exe.</p>");

        return ToHtml(File.ReadAllText(path));
    }

    public static string ToHtml(string markdown) =>
        Wrap(Markdown.ToHtml(markdown, Pipeline));

    private static string Wrap(string bodyHtml) =>
        """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <style>
          html, body { margin: 0; padding: 0; background: #ffffff; color: #1a1a1a; }
          body { font-family: Segoe UI, Arial, sans-serif; font-size: 14px; line-height: 1.55; padding: 20px 28px 32px; max-width: 920px; }
          h1, h2, h3 { line-height: 1.25; margin-top: 1.4em; margin-bottom: 0.5em; }
          h1 { font-size: 1.6em; margin-top: 0; }
          p, li { margin: 0.5em 0; }
          ul, ol { padding-left: 1.4em; }
          code { font-family: Consolas, monospace; background: #f3f3f3; padding: 1px 4px; border-radius: 4px; }
          pre { background: #f5f5f5; padding: 12px; border-radius: 8px; overflow: auto; }
          pre code { background: transparent; padding: 0; }
          a { color: #1a73e8; }
          hr { border: none; border-top: 1px solid #ddd; margin: 1.5em 0; }
          strong { color: #000000; }
          table { border-collapse: collapse; margin: 1em 0; max-width: 100%; }
          th, td { border: 1px solid #ddd; padding: 6px 10px; text-align: left; vertical-align: top; }
          th { background: #f5f5f5; font-weight: 600; }
          td code { white-space: nowrap; }
        </style>
        </head>
        <body>
        """ + bodyHtml + """
        </body>
        </html>
        """;
}
