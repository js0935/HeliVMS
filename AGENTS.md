# AGENTS.md

Repo conventions for AI agents and new contributors. Read this before making changes.

## What this is

HeliVMS is a Windows NVR/VMS. Camera ingest is RTSP → ffmpeg → H.264 RTP → WebRTC
(WHEP) to browsers. Recordings go to segmented MP4.

- .NET 10 (`HeliVMS.slnx`), Node 24 for the web SPA only.
- The web SPA (`src/HeliVMS.Web`) has **zero runtime npm dependencies**. It is plain
  ES modules + vanilla JS. Do not introduce a framework or a CDN script — the page
  ships a strict `Content-Security-Policy` with `default-src 'self'`.
- SIPSorcery 10.0.16 is the WebRTC stack. `WhepPeer` is the only place SIPSorcery is
  constructed, deliberately, so negotiation failures have one origin.

## Build and test

Run from the repo root.

```
dotnet build HeliVMS.slnx -c Release --nologo
dotnet test  HeliVMS.slnx -c Release --no-build --nologo
cd src/HeliVMS.Web; npm ci; npm test
```

Notes that will save you time:

- **`dotnet test` on the whole solution is not reliable here.** `HeliVMS.Media` and
  `HeliVMS.Storage` tests create real files, and `HeliVMS.Rtc` spawns real ffmpeg
  processes. Under load they contend and intermittently fail. CI runs the test
  projects **one at a time**; do the same when a full-suite run fails, before
  concluding anything is broken.
- ffmpeg must be on `PATH`. Tests that need it are `[SkippableFact]` and self-skip
  via `PublishPipelineProbe`, so a green run without ffmpeg means less than it looks.
- `HeliVMS.LicenseProducer.Tests` targets `net10.0-windows`.
- Commits go through `.github/workflows/ci.yml`. Check the run before calling work done.

## Frontend conventions

- User-visible strings go through `t()` / `tx()` and live in the i18n catalogue.
  Never hardcode a user-facing string in markup or JS.
- Anything interpolated into an `innerHTML` template must be escaped with `esc()`
  (text/attribute context) or `escPct()` (chart geometry). `app.security.test.js`
  enforces this by scanning every `innerHTML` assignment.
- All network calls go through `apiRaw` in `app.js` — exactly one `fetch` call site,
  which is what keeps the bearer header consistent. `app.security.test.js` pins that count.
- Layout behavior lives in `styles.css` and is pinned by `app.layout.test.js`.
  If you change CSS, add or update an assertion there; a silent layout regression
  is exactly what the operators reported and nothing else would catch it.
- Line endings: `.js`/`.css`/`.html` in `src/HeliVMS.Web` are CRLF except `*.test.js`,
  which are LF. Match the file you are editing.

## Test conventions

- Test names are Traditional Chinese sentences describing the guarantee, not the
  method: `第一個RTP封包到達前不會回應`, not `TestNoResponseBeforeFirstPacket`.
  The name shows up in CI output, so it should tell an operator what broke.
- Comments explain **why**, especially why a non-obvious approach was chosen and what
  the failure looks like in production. A test that guards a real incident should say
  what the symptom was.
- Prefer behavioral assertions. Source-text scanning (grepping a `.cs` for a string)
  is brittle and usually a sign the real seam is missing.
- Verify a new guard actually guards: mutate the code it protects, confirm the test
  fails, then revert. A test that passes against broken code is worse than no test.

## Known landmines

- **UDP port selection in tests.** Never pick a port by binding a probe socket and
  disposing it — xunit runs test classes in parallel, so another class can take that
  port. Bind the real socket first and hold it. This caused
  `第一個RTP封包到達前不會回應` to fail only on CI.
- **ffmpeg RTP output must stay `-f rtp`.** `-f rte` (RTCP only) produces no media.
  `PublisherIntegrationTests` fails if this regresses.
- **`OpenAsync` must not complete before the first RTP packet.** Returning 201
  immediately gives the browser a connection that looks successful and never shows
  video, with nothing in the logs. Guarded by `第一個RTP封包到達前不會回應`.
- **`X_DisableExtendedMasterSecretKey = true`** is deliberate: pure-managed SharpSRTP
  uses EMS by default (RFC 7627) but some older Safari only accepts RFC 5764.
- **Do not add trickle ICE.** There is no `PATCH` endpoint; the answer must carry
  complete candidates or the browser has no path to the server.
- **Encoding parameters are load-bearing.** payload type 96, H.264/90000,
  `packetization-mode=1`, `profile-level-id=42e01f`. Any mismatch means "connects,
  no picture" with a clean log. `WhepLoopbackTests` asserts the negotiated SDP and
  that ICE + DTLS really complete against it.