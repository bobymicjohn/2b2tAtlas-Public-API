# Running your own Atlas

The default launcher binds only to loopback. Before internet exposure, configure HTTPS,
the public origins, filesystem permissions, a private backup destination and tested
restore procedures. Use dedicated non-administrator service accounts. Do not put the
database, `.local`, collector profiles, work directories or secrets under a static root.

JWT and worker secrets are different random values. The worker receives its raw key;
the API stores its SHA-256. The initial owner has a generated password, and a fresh
database refuses startup without a supplied bootstrap password. Existing accounts are
never promoted or reset by startup. Create collaborators explicitly through Admin.

Authenticated writes retain permission checks, owner-only destructive operations,
edit quotas, verified pre-edit SQLite backups, audit records, highway version conflicts
and targeted rollback. These controls do not replace host backups. Failed recovery
storage checks deliberately pause edits; do not bypass them to get a green status.

WDLs are untrusted ZIP/NBT inputs. Keep inspection, extraction and rendering outside the
API process. Pin and hash-check renderers, bound resources and retain immutable source
archives. Keep final chunk coverage checks enabled. Test restore using a disposable
database before inviting contributors.

Before sharing changes, run `python scripts/check-public-export.py` and a secret scanner
against staged files and Git history. The custom guard detects runtime files and known
unsafe patterns; a passing result is not a guarantee. Never post credentials or private
worlds in GitHub issues. Follow the repository's root security reporting instructions.
