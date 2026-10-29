// =====================================================================================
// FILE PURPOSE (in plain terms):
//   The sign-in page for the console's access token. It's a single self-contained HTML
//   string (no separate CSS/JS files) generated at request time so it can show an "invalid
//   token" message — kept out of Program.cs just to keep that file focused on wiring.
// =====================================================================================

namespace Orchestrator.Console;

public static class LoginPage
{
    public static string Build(bool error) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Orchestrator Console — Sign in</title>
          <style>
            /* Same green-on-black terminal look as the console page itself. */
            * { box-sizing: border-box; }
            body { margin: 0; font: 13px/1.55 ui-monospace, "Cascadia Mono", Consolas, "Courier New", monospace;
                   background: #020803; color: #b8ffcc; display: flex; align-items: center;
                   justify-content: center; height: 100vh; accent-color: #00ff66; color-scheme: dark; }
            form { background: rgba(3, 18, 8, .9); border: 1px solid #0f3d1f; border-radius: 4px; padding: 28px; width: 300px;
                   box-shadow: 0 0 30px rgba(0, 255, 102, .08), inset 0 0 24px rgba(0, 255, 102, .04); }
            h1 { font-size: 14px; margin: 0 0 16px; color: #00ff66; text-transform: uppercase; letter-spacing: .08em;
                 text-shadow: 0 0 6px rgba(0, 255, 102, .55); }
            h1::before { content: '> '; color: #4f9e66; }
            h1::after { content: '_'; animation: blink 1.1s steps(1) infinite; }
            @keyframes blink { 50% { opacity: 0; } }
            @media (prefers-reduced-motion: reduce) { h1::after { animation: none; } }
            input { width: 100%; padding: 8px 10px; border: 1px solid #0f3d1f; border-radius: 3px; font: inherit;
                    background: #000; color: #b8ffcc; caret-color: #00ff66; }
            input:focus-visible { outline: 1px solid #00ff66; }
            input::placeholder { color: #2f5c3d; }
            button { width: 100%; margin-top: 12px; padding: 8px; border: none; border-radius: 3px;
                     background: #00ff66; color: #001a09; font: inherit; font-weight: 700; cursor: pointer; }
            button:hover { box-shadow: 0 0 14px rgba(0, 255, 102, .7); }
            .err { color: #ff3b5c; font-size: 12px; margin-top: 10px; }
            .err::before { content: '[!] '; }
          </style>
        </head>
        <body>
          <form method="post" action="/login">
            <h1>Orchestrator Console</h1>
            <input type="password" name="token" placeholder="Access token" autofocus autocomplete="current-password" />
            <button type="submit">Sign in</button>
            {{(error ? "<div class=\"err\">Invalid token.</div>" : "")}}
          </form>
        </body>
        </html>
        """;
}
