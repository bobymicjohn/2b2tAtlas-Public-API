# WDL ingestion security review

> **AI-Generated documentation.**

## Security objective

Assume an archive is intentionally crafted to execute code, overwrite files, exhaust CPU/RAM/disk, poison published metadata, steal credentials, or create a subtly incorrect map. The public Atlas server must remain outside the extraction and rendering trust boundary.

## Hostile-actor analysis

| Attack | Defense | Residual risk / operation |
| --- | --- | --- |
| `../`, rooted, drive, UNC, or backslash traversal | normalize separators, reject rooted and dot segments, recompute contained destination | keep work root off sensitive volumes |
| Windows device names and ambiguous trailing characters | reject `CON`, `NUL`, `COM1`, etc., colons, trailing dots/spaces | extraction is portable and deterministic |
| duplicate/case-colliding/Unicode-equivalent names | NFC normalization and case-insensitive uniqueness | exact Unicode confusables remain possible in harmless display names |
| symlink, device, FIFO, socket | reject non-regular Unix file modes | ZIP metadata differs across producers; unknown types fail closed |
| encrypted or unsupported ZIP | .NET reader failure becomes rejection; no password handling | central-directory encryption variants require corpus tests |
| ZIP bomb | archive, entry, expanded-total, count, and ratio limits; streamed copy verifies declared size | CPU cost within allowed ratio remains possible |
| proxy/body-size rejection | ordered 32 MiB chunks, exact offsets, declared total, owner binding, and 24-hour cleanup | interrupted browser sessions restart rather than resume across devices |
| Archive outage or duplicate source | require a verified SHA-addressed E object before queueing; accepted duplicates converge | NAS X availability affects backups, not normal ingestion |
| archive mutation during intake | private SHA-256-named snapshot followed by hash reinspection | source can change while copying, but snapshot is revalidated and becomes authoritative |
| partial/stale extraction | random `.partial` directory and atomic rename; explicit prepare retry revalidates the snapshot and preserves prior artifacts | retained attempts consume quota and require deliberate retention policy |
| disk exhaustion | declared expansion check, free-space reserve, output byte cap, log cap | renderer can create many non-tile files before `verify`; OS quota is still recommended |
| parser stack/memory abuse | bounded NBT expansion, depth, element count, collection lengths, and UTF-8 validation | region-file internals are parsed by uNmINeD, not this process |
| renderer command injection | fixed trusted argument arrays and `ArgumentList`; no shell | renderer profile is privileged configuration |
| trojan renderer | executable basename and mandatory SHA-256 pin | a pinned malicious binary remains malicious; verify source/signature separately |
| stale/mixed renderer resume | provenance binds plan, world, dimension, renderer version/hash, and exact profile arguments | renderer-internal resume correctness still depends on the pinned release |
| signed/native tile misplacement | named adaptation schemes, canonical native path parsing, bounded image headers, aligned parent generation, and an adaptation receipt | each dimension/extent scheme still requires a real coordinate fixture |
| renderer escape/RCE through malformed chunks | dedicated low-privilege account or container; minimal environment; no secrets; timeout and tree kill | .NET cannot provide a Windows sandbox by itself |
| secret theft | renderer starts before registration token is introduced; child environment is cleared; token read only for HTTP stage | run registration in a separate process/session for strongest isolation |
| log flooding | 64 MiB cap per invocation and process termination | concurrent stdout/stderr share a stream; ordering is nondeterministic but bounded |
| arbitrary URL / stored script metadata | shared server validator allowlists scheme, host, port, path prefix, placeholders, and field bounds | CSP and client-side URL handling remain defense-in-depth |
| unexpected renderer web assets | publish only an exact `z/y/x` tile subtree with one image format; reject links, extra paths, gaps, and duplicate coordinates | image decode and semantic correctness remain separate checks |
| publish race / half-written tiles | day/night publish under a new immutable generation; DB URL switches only after both verify | CDN/cache warmup is outside filesystem atomicity |
| failed re-render | previous generation remains registered until replacement succeeds; failed generation is deleted/restored | superseded cleanup is best-effort after registration |
| register-before-publish or destination tampering | receipt binds the job/plan/dimension plus a deterministic SHA-256 of every tile path, length, and byte; registration rehashes the destination | verification cost scales with published bytes; keep the destination read-only |
| accidental public exposure | registration forces `IsPublished=false`; human promotion is separate | protect `renders.manage` and issue short-lived tokens |
| wrong-but-valid coordinates | mandatory known-point and overlap review before promotion | fixture-based coordinate validation is future work |
| malicious archive committed to Git | `.gitignore` excludes ZIPs, worlds, chunks, tools, work, output, and secrets | use repository secret scanning and review `git status` |
| compromised/changed Archive GUI | bounded GUI contract, exact clicked leaf, durable checkpoints, exclusions, shared single-client lock | stop on changed menus or unexpected dimension transitions; never infer a leaf from display text alone |
| stale or malicious Archive identity metadata | preserve exact warp/catalog/report provenance; deterministic bounded candidate set; conflicts become `needs-match` | provenance supports identity but is not cryptographic proof of original 2b2t origin |
| collector account/token exposure | isolated HeadlessMc profile; no tokens in scripts, logs, repository, or Atlas database | protect the Windows profile and rotate Microsoft sessions if the collector directory is exposed |
| capture-to-publication confusion | `captured` quarantine, explicit `ready` promotion, separate SHA-verifying importer, normal hostile-input pipeline | operator batch selection is still an approval boundary |
| BlueMap derivative mutates or expands a historical WDL | source ZIP is hash-verified and read-only; relighting occurs in a disposable copy; generated neighbors are pruned; source/final Anvil block-chunk inventories must be exactly equal | malformed entity/POI sidecars may be omitted only from the derivative, never the preserved source |
| partial or unsafe 3D output is guessed public | immutable generation manifest, minimum profile, static payload, camera, lighting, and exact-footprint gates; API/static middleware advertise only a passing generation | preserve failed/superseded generations privately for diagnosis and create a new generation |

## Required deployment isolation

For production WDL processing:

- Use a dedicated non-administrator OS account with no interactive secrets and no write access outside intake/work/publish staging.
- Put work on a dedicated volume with an OS quota. The code's byte accounting is not a substitute for a quota against renderer bugs.
- Deny outbound network access during `prepare` and `render` unless the pinned renderer demonstrably requires it.
- Keep the published web root read-only to the web server. The ingestor writes a separate staging parent and atomically hands off immutable trees.
- Run registration only after rendering exits, preferably in a fresh process with a short-lived scoped JWT.
- Apply malware scanning to inputs as defense-in-depth, but never treat a clean scan as archive validation.
- Patch the OS, .NET runtime, and renderer. Changing the renderer requires a new profile/hash and coordinate acceptance run.
- Keep the outbound-connected Archive collector separate from the extraction/render account. Cataloging or successful WDL save must never grant write access to published tiles or Atlas metadata.
- Keep BlueMap scratch, tool cache, lock/checkpoint state, immutable output, and X source objects in their documented separate roots. Give the API read-only access to completed BlueMap output and never expose scratch or manifests by directory listing.

On Windows, a dedicated local account plus NTFS ACLs and Defender/WDAC controls is the minimum practical boundary. For stronger containment, use a disposable VM with a data-only output handoff. Do not grant the renderer access to the Atlas database, source repository, SSH keys, browser profile, cloud credentials, or API token.

## Failure policy

Fail closed on unknown archive formats, unknown manifest/profile keys, missing dimensions, hash mismatch, existing output, unapproved URL roots, malformed dates, renderer timeout, nonzero exit, tile-layout mismatch, or insufficient disk. Operators may raise documented resource limits or create a new verified renderer profile; they should not bypass path, hash, URL, or publication checks.

## Test coverage

`2b2tAtlas.Ingestor.Tests` currently exercises:

- slash and backslash traversal;
- rooted and drive paths;
- reserved names and ambiguous segments;
- case-colliding paths;
- Unix symlinks;
- high compression ratio;
- corrupt archives;
- safe nested extraction;
- bounded metadata parsing across Anvil, McRegion, and Alpha layouts;
- valid and out-of-bounds tile coordinates;
- atomic publication;
- exact tile-tree grammar, contiguous zooms, and consistent image format;
- provenance-bound renderer resume and missing-provenance rejection;
- signed native-tile adaptation across positive/negative coordinates;
- snapshot-bound prepare retry with preserved forensic artifacts;
- job/plan/dimension-bound publication receipts and destination tamper detection;
- hostile schemes, host confusion, wrong prefixes, incomplete placeholders, query redirects, and day/night mismatch.
- chunk offset/finalization, WDL archive dedup/tamper rejection, world-root traversal, and dual `{dn}` registration.

Run with:

```powershell
dotnet test .\2b2tAtlas.Ingestor.Tests\2b2tAtlas.Ingestor.Tests.csproj
```

Before production, maintain a quarantined corpus for encrypted ZIPs, ZIP64 edge cases, truncated entries, metadata mismatches, very large entry counts, malformed NBT at every tag type, malformed region files, and representative WDLs from each supported era. Those binary fixtures should live outside Git or in a dedicated small-fixture repository.

## Known limitations

- Only ZIP intake is accepted. Convert RAR/7z/tar in a separate untrusted conversion sandbox, then ingest the ZIP.
- The .NET layer inventories region/chunk storage but does not parse region contents; renderer vulnerabilities remain in the renderer boundary.
- Renderer profiles are operator-authored because documented uNmINeD flags and output layout vary. The example intentionally configures only the officially documented command.
- Tile verification checks layout, coordinate bounds, and size, not PNG/WebP decode integrity or geographic correctness.
- Work-directory ACLs and disk quotas are operational prerequisites, not configured by the cross-platform CLI.
- The server's allowed URL prefixes must be reviewed whenever tile hosting changes. An empty allowlist rejects every registration.
- Bedrock Edition and custom/modded dimensions are not supported. Only Java `.`, `DIM-1`, and `DIM1` are recognized.
