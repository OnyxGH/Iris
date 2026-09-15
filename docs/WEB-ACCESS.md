# Web access

Iris ships a built-in extension (`src/Iris.WebAccess`) that gives the model three tools:

| Tool | Does |
|------|------|
| `web_search` | Searches the web for one query or several (`queries`, three at a time). Returns answers with source links. With `includeContent: true` it fetches the result pages in the background and tells the model when they are ready. With `workflow: "summary-review"` it opens the curator so you can pick the results and approve the summary the model receives. |
| `fetch_content` | Fetches one or more URLs. HTML pages are reduced to their readable article and converted to Markdown, PDFs are extracted to a Markdown file in the temp directory, and GitHub repository URLs are shallow-cloned so the model can explore real files. `mode: "raw"` returns text responses unchanged. |
| `get_search_content` | Reads stored results by `responseId`: a query's results, slices of fetched content (`offset`/`limit`), or passages matching `findText` (exact, case-insensitive or fuzzy). |

It works without configuration: search uses Exa's public MCP endpoint, which needs no API key. Disable the whole
extension with `"builtinExtensions": { "web-access": false }` in settings.

## Configuration

Options live in `web-search.json` in the agent directory (`~/.iris/agent/web-search.json`). Changes apply without a
restart.

```json
{
  "provider": "auto",
  "exaApiKey": "$EXA_API_KEY",
  "braveApiKey": "!op read op://Private/Brave/credential",
  "searxngBaseUrl": "https://search.example.com",
  "maxInlineContentChars": 30000
}
```

### Search providers

In `auto` mode (the default) `web_search` tries, in order, the first that is configured and succeeds:

1. SearXNG (`searxngBaseUrl` / `SEARXNG_BASE_URL`, optional `searxngHeaders`)
2. Exa (`exaApiKey` / `EXA_API_KEY` for the API; otherwise the keyless MCP endpoint)
3. Brave (`braveApiKey` / `BRAVE_API_KEY`)
4. Tavily (`tavilyApiKey` / `TAVILY_API_KEY`)
5. Perplexity (`perplexityApiKey` / `PERPLEXITY_API_KEY`)

DuckDuckGo needs no key but is only used when selected explicitly. Set `provider` to one of `exa`, `brave`, `tavily`,
`perplexity`, `searxng`, `duckduckgo`, to `all` (every configured provider except DuckDuckGo, in parallel), or to a list
of providers. `webSearch.allowedProviders` restricts which providers the model may request. Base URLs can be overridden
with `exaBaseUrl`, `braveBaseUrl`, `tavilyBaseUrl` or the matching `*_BASE_URL` environment variables.

API key values can be literal, `$NAME` or `${NAME}` (an environment variable), or `!command` (the command's output, for
password managers). Start a literal with `$$` or `$!` to escape it.

### Curator

`web_search` takes a `workflow`:

| Workflow | Does |
|----------|------|
| `none` (default) | Returns the results to the model. |
| `summary-review` | Writes a summary draft, then opens the curator modal so you can review it. What you approve is what the model sees. |
| `auto-summary` | Writes the summary and returns it without opening the curator. |

Set a different default with `"workflow": "summary-review"` in web-search.json. Without a terminal (print, json and rpc
modes) a review falls back to `auto-summary`.

In the curator, ↑↓ move, space toggles the query or source under the cursor, tab folds a query, and a/n select or clear
everything. Deselected sources are dropped from the results the model receives; a query with no sources left is dropped
too. g writes the summary again from the current selection, f asks for feedback and regenerates with it, e opens the
summary in the editor, page up/down scroll it, enter submits, and escape cancels the search.

Summaries are written by the first model that answers of: `summaryModel` in web-search.json (`provider/model-id`),
Claude Haiku 4.5, Gemini 3.6 Flash, GPT-5 mini, DeepSeek V4 Flash, then the session's own model — restricted to models
you have configured. When no model is available, or generation fails or takes longer than 30 seconds, the curator shows
a deterministic outline of the selected results instead, and says so.

### Fetching

| Key | Default | Meaning |
|-----|---------|---------|
| `fetch.timeoutMs` | 30000 | Timeout for fetching one URL. |
| `maxInlineContentChars` | 30000 (max 200000) | Characters of fetched content returned inline; the rest is read with `get_search_content`. |
| `pdf.enabled`, `pdf.maxSizeMB`, `pdf.maxPages` | true, 20 (max 50), 100 | PDF extraction. |
| `githubClone.enabled`, `githubClone.maxRepoSizeMB`, `githubClone.cloneTimeoutSeconds`, `githubClone.clonePath` | true, 350, 30, temp dir | Cloning GitHub repositories. Uses `gh` when installed (also for private repositories and for the size check), otherwise `git`. |
| `fetchContent.domainPolicy.allow` / `.deny` | none | Hostnames (and their subdomains) fetch_content may or may not fetch. |

Fetched content is kept in `web-search-cache/` in the agent directory for an hour (at most 128 entries or 128 MB); the
session only records metadata.

### Network safety

Fetched URLs and self-hosted endpoints (SearXNG) may not reach private, loopback, link-local or other reserved
addresses. The check runs before the request, on every redirect and again when connecting, so DNS rebinding cannot
bypass it. To allow a range, for example a local SearXNG or a TUN/fake-IP proxy, add it to `ssrf.allowRanges`:

```json
{ "ssrf": { "allowRanges": ["127.0.0.1", "198.18.0.0/15"] } }
```

With an HTTP(S) proxy configured through `HTTP_PROXY`/`HTTPS_PROXY`, `ssrf.trustEnvProxy: true` skips the local DNS
check for proxied hosts (literal private addresses and localhost stay blocked).

## Not yet ported

OpenAI, Gemini and the other hosted search providers, YouTube and video understanding, page answer mode, hosted page
extraction fallbacks (Firecrawl, Jina, ...), authenticated fetching with browser cookies, per-call proxies, source
checking, and the activity widget. pi's curator runs in a browser; Iris reviews searches in the terminal instead.
