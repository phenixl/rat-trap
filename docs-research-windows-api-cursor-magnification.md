# Windows API doc research — Magnification / Cursor APIs / ANI format

**Deliverable status: BLOCKED — ZERO authoritative pages could be retrieved.**
No quote in this report is a quote. Every "exact quote" item is marked
`NOT RETRIEVABLE`. Nothing was paraphrased and passed off as documentation.

---

## A. Environment blocker (why there are no quotes)

| Fact | Evidence |
| --- | --- |
| All DNS answers are fake-IP (`198.18.0.0/15`), including fabricated hostnames | `nonexistent-zzz-12345.example -> 198.18.1.89`; `learn.microsoft.com -> 198.18.1.86`; `raw.githubusercontent.com -> 198.18.0.239`. Resolver = Clash for Windows (PID 21452) TUN fake-IP mode, which also answers queries sent to `8.8.8.8`. |
| Outbound HTTPS/TLS is impossible from the shell | `curl.exe` → `schannel: AcquireCredentialsHandle failed: SEC_E_NO_CREDENTIALS (0x8009030e)`; .NET `SslStream.AuthenticateAsClient` → `Authentication failed, see inner exception`. Both direct and via proxy `127.0.0.1:7890`; hosts tried: `1.1.1.1`, `github.com`, `learn.microsoft.com`. |
| Plain HTTP egress **does** work (so the block is TLS-specific) | `GET http://learn.microsoft.com/...` returned `301` with `Server: AkamaiGHost`, `Location: https://learn.microsoft.com/...`, `Akamai-Cache-Status: Redirect from child` — real Akamai edge, HTTPS-only redirect. |
| `web_fetch` refuses every external host | `Error: URL hostname "learn.microsoft.com" resolves to a non-public IP address` (also for `example.com`, `r.jina.ai`, `cdn.jsdelivr.net`, `raw.githubusercontent.com`). |
| `web_search` returns URL lists only, no page text or snippets | Two queries returned 16 results, all titles+URLs, zero quoted doc text. |

Conclusion: there is no working path to page text in this session. This is the same
TLS/schannel boundary the tooling docs describe for confined sandbox modes; per
instructions it was not retried with other tricks after being identified.

---

## B. Fetched URLs

### B.1 URLs actually fetched — all failed, zero bytes of content

| # | URL | Tool | Result |
| --- | --- | --- | --- |
| 1 | https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetwindowfilterlist | web_fetch | ERROR non-public IP |
| 2 | https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magshowwindow | web_fetch | ERROR non-public IP |
| 3 | https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magshowcursor | web_fetch | ERROR non-public IP |
| 4 | https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetsystemcursor | web_fetch | ERROR non-public IP |
| 5 | https://raw.githubusercontent.com/MicrosoftDocs/win32/refs/heads/docs/desktop-src/WinAuto/magapi/magapi-magnifier-styles.md | web_fetch | ERROR non-public IP |
| 6 | https://example.com/ | web_fetch | ERROR non-public IP |
| 7 | https://r.jina.ai/https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetwindowfilterlist | web_fetch | ERROR non-public IP |
| 8 | https://cdn.jsdelivr.net/gh/MicrosoftDocs/win32@docs/desktop-src/WinAuto/magapi/magapi-magnifier-styles.md | web_fetch | ERROR non-public IP |
| 9 | (shell) `GET http://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetwindowfilterlist` | Invoke-WebRequest/HttpWebRequest | 301 AkamaiGHost → no body |
| 10 | (shell) `GET https://example.com/`, `https://learn.microsoft.com/...` (+ via `-x http://127.0.0.1:7890`, `socks5://127.0.0.1:7890`) | curl.exe / .NET | TLS failure (see A) |
| 11 | (shell) `Test-Tls` to `1.1.1.1`/`cloudflare-dns.com`, `140.82.121.4`/`github.com`, `198.18.1.86`/`learn.microsoft.com` | SslStream | TLS failure (see A) |

### B.2 Candidate URLs surfaced by web_search (NOT fetched — content never seen)

These are the authoritative targets to retrieve once egress works. Inclusion is not
evidence that any statement in section C is true.

Magnification API:
- https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetwindowfilterlist
- https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magshowsystemcursor
- https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetimagescalingcallback
- https://learn.microsoft.com/en-us/windows/win32/winauto/magapi/magapi-magnifier-styles
- https://learn.microsoft.com/en-us/windows/win32/winauto/magapi/entry-magapi-functions
- https://raw.githubusercontent.com/MicrosoftDocs/win32/refs/heads/docs/desktop-src/WinAuto/magapi/entry-magapi-functions.md
- https://raw.githubusercontent.com/MicrosoftDocs/win32/refs/heads/docs/desktop-src/WinAuto/magapi/magapi-magnifier-styles.md
- https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/magnification/nf-magnification-magsetimagescalingcallback.md

---

## C. Requested items — raw findings

Format: **claim** → **exact quote** → **URL**.

### C.1 Magnification API (`Magnification.dll`, `magnification.h`)

| # | Claim requested | Exact quote | URL |
| --- | --- | --- | --- |
| 1 | `MagSetWindowFilterList` signature | NOT RETRIEVABLE | (B.2 #1) |
| 2 | `dwFilterMode` semantics for `MW_FILTERMODE_EXCLUDE` / `MW_FILTERMODE_INCLUDE` | NOT RETRIEVABLE | (B.2 #1) |
| 3 | Which function takes `MS_SHOWMAGNIFIEDCURSOR`, and its text | NOT RETRIEVABLE | (B.2 #4) |
| 4 | `MagShowSystemCursor` exact doc text | NOT RETRIEVABLE | (B.2 #2) |
| 5 | `MagSetWindowSource` exact doc text | NOT RETRIEVABLE | not located |
| 6 | `MagGetWindowSource` exact doc text | NOT RETRIEVABLE | not located |
| 7 | Does the magnifier render into a real HWND or expose a bitmap | NOT RETRIEVABLE | (B.2 #5, #6) |
| 8 | `MagSetImageScalingCallback` deprecated/removed? since which version | NOT RETRIEVABLE | (B.2 #3) |
| 9 | Any Magnification API that returns cursor bitmap / alpha / position | NOT RETRIEVABLE — full function inventory could not be read, so "none exists" is NOT established | (B.2 #5, #6) |
| 10 | `MagInitialize` / `MagUninitialize` requirements note | NOT RETRIEVABLE | not located |
| 11 | Statement that the magnifier window must be a real visible window | NOT RETRIEVABLE | (B.2 #6) |

### C.2 Cursor APIs

| # | Claim requested | Exact quote | URL |
| --- | --- | --- | --- |
| 12 | `GetCursorInfo` + `CURSORINFO` exact fields | NOT RETRIEVABLE | not located |
| 13 | Ownership/sharing note for `CURSORINFO.hCursor` (destroy or not) | NOT RETRIEVABLE | not located |
| 14 | `GetIconInfo` + `ICONINFO` fields (`hbmMask`, `hbmColor`, `xHotspot`, `yHotspot`) | NOT RETRIEVABLE | not located |
| 15 | Note on `hbmColor == NULL` for monochrome cursors | NOT RETRIEVABLE | not located |
| 16 | Which bitmap carries the alpha channel | NOT RETRIEVABLE | not located |
| 17 | `CopyIcon` vs `CopyImage` doc text and differences | NOT RETRIEVABLE | not located |
| 18 | `GetIconInfoEx` signature + purpose | NOT RETRIEVABLE | not located |
| 19 | `CreateIconFromResourceEx` signature + purpose | NOT RETRIEVABLE | not located |
| 20 | `LookupIconIdFromDirectoryEx` signature + purpose | NOT RETRIEVABLE | not located |
| 21 | `GetSystemMetrics(SM_CXCURSOR / SM_CYCURSOR)` documented meaning | NOT RETRIEVABLE | not located |
| 22 | `DrawIconEx` + `DI_NORMAL` alpha behavior | NOT RETRIEVABLE | not located |
| 23 | Animated cursors: which frame is drawn | NOT RETRIEVABLE | not located |

### C.3 Sizing, accessibility pointer size, `.ani` format

| # | Claim requested | Exact quote | URL |
| --- | --- | --- | --- |
| 24 | Rendered system-cursor size vs accessibility pointer-size setting | NOT RETRIEVABLE | not located |
| 25 | Whether `GetIconInfo` returns scaled or unscaled bitmaps | NOT RETRIEVABLE | not located |
| 26 | `.ani` RIFF/`ANIH`/`ACON` format: `ANIH` fields | NOT RETRIEVABLE | not located |
| 27 | `.ani` flags (`AF_ICON`, `AF_SEQUENCE`, `AF_DISPLAY`) | NOT RETRIEVABLE | not located |
| 28 | `rate ` chunk semantics | NOT RETRIEVABLE | not located |
| 29 | `seq ` chunk semantics | NOT RETRIEVABLE | not located |

**Important distinction:** the `.ani` container format is documented by Microsoft as a
RIFF/Windows-media format, not on a Learn "win32/api" reference page, so the right
targets differ from section B.2. No such page was located or fetched.

---

## D. What is NOT DOCUMENTED vs NOT RETRIEVABLE

- **NOT DOCUMENTED** = the authoritative page was read and does not state the item.
  This report contains **no** NOT DOCUMENTED verdicts, because no page was read.
- **NOT RETRIEVABLE** = retrieval failed in this environment; the item's documentation
  status is unknown. All 29 items are NOT RETRIEVABLE.

Do not treat any row above as evidence for or against the existence of a documented
Magnification cursor-bitmap API.

---

## E. Recommended next step for the parent

Re-run the same fetch set from an environment where HTTPS egress from the shell works or
where the fetch tool is not restricted to public IPs, using section B.2 as the exact
target list. Bulk text of the Magnification reference pages is also available verbatim
in the `MicrosoftDocs/sdk-api` and `MicrosoftDocs/win32` Git repositories
(`sdk-api-src/content/magnification/*.md`,
`desktop-src/WinAuto/magapi/*.md`), which is the cheapest single source for exact
quotes if raw-file retrieval is possible.
