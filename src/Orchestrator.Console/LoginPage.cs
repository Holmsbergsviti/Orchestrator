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
            * { box-sizing: border-box; }
            body { margin: 0; font: 14px/1.5 system-ui, -apple-system, Segoe UI, Roboto, sans-serif;
                   background: #f6f7f9; color: #1b1f24; display: flex; align-items: center;
                   justify-content: center; height: 100vh; }
            form { background: #fff; border: 1px solid #e5e7eb; border-radius: 12px; padding: 28px; width: 280px; }
            h1 { font-size: 15px; margin: 0 0 16px; }
            input { width: 100%; padding: 8px 10px; border: 1px solid #e5e7eb; border-radius: 8px; font: inherit; }
            button { width: 100%; margin-top: 12px; padding: 8px; border: none; border-radius: 8px;
                     background: #2563eb; color: #fff; font: inherit; cursor: pointer; }
            .err { color: #dc2626; font-size: 12px; margin-top: 10px; }
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
