"""Inspect staged changes, a commit range or a snapshot without printing sensitive values."""

import argparse
import re
import subprocess
from pathlib import Path


SCRIPT_ROOT = Path(__file__).resolve().parents[1]
PRIVATE_PATH = re.compile(
    r'(?i)(?:^|/)(?:commerce|payments?)(?:/|\.)|'
    r'(?:^|/)(?:Shop|Checkout|Advertising|Stripe|Payment)[^/]*\.(?:cs|razor|js|json|sql)$'
)
SECRET_PATTERNS = [
    r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
    r'\bgh[pousr]_[A-Za-z0-9]{25,}',
    r'\bgithub_pat_[A-Za-z0-9_]{35,}',
    r'\beyJ[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}',
    r'(?i)[A-Z]:[\\/]+Users[\\/]+(?!atlas-operator\b|example\b)[^\\/\s"\']+',
]


class CheckError(Exception):
    """An operational failure with a safe, value-free diagnostic."""


def git(*arguments):
    try:
        return subprocess.check_output(
            ['git', *arguments], cwd=SCRIPT_ROOT, stderr=subprocess.PIPE
        )
    except (OSError, subprocess.CalledProcessError) as error:
        raise CheckError('Git inspection failed; no files were approved.') from error


def resolve_commit(value):
    try:
        return git('rev-parse', '--verify', '--end-of-options', value + '^{commit}').decode().strip()
    except CheckError as error:
        raise CheckError('The supplied revision is not a readable commit; no files were approved.') from error


def inspect_version(path, data):
    errors = []
    # Preserve the existing private-code policy in every invocation mode.
    if PRIVATE_PATH.search(path):
        errors.append('Private feature path: ' + path)
    if re.search(r'(?i)\.(?:db|sqlite3?|pfx|p12|key|env)$', path):
        errors.append('Private/runtime material: ' + path)
    try:
        text = data.decode('utf-8-sig')
    except UnicodeDecodeError:
        return errors
    if any(re.search(pattern, text) for pattern in SECRET_PATTERNS):
        errors.append('Inspect possible private content: ' + path)
    if path.endswith(('.cs', '.razor', '.csproj', '.js')) and re.search(
        r'\b(?:ShopListing|ShopService|PaymentService|StripeClient|CheckoutSession)\b', text
    ):
        errors.append('Private feature wiring: ' + path)
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', type=Path, help="Legacy hook repository root; must be this script's Git root")
    parser.add_argument('--base', help='Base commit for staged or historical changes (default: origin/main)')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--head', help='Inspect changed files in every commit after --base, otherwise inspect the index')
    mode.add_argument('--ref', help='Legacy hook mode: inspect the entire supplied commit snapshot')
    args = parser.parse_args()
    if args.ref and args.base:
        parser.error('--ref scans a complete snapshot and cannot be combined with --base')

    try:
        actual_root = Path(git('rev-parse', '--show-toplevel').decode().strip()).resolve()
        if actual_root != SCRIPT_ROOT:
            raise CheckError('The check must live in the scripts directory of its repository root.')
        if args.repo is not None and args.repo.resolve() != actual_root:
            raise CheckError('--repo must identify this script\'s repository root; no files were approved.')
        if args.ref:
            revisions = [resolve_commit(args.ref)]
        else:
            base = resolve_commit(args.base or 'origin/main')
            revisions = (
                git('rev-list', '--reverse', base + '..' + resolve_commit(args.head)).decode().splitlines()
                if args.head else [None]
            )

        errors = []
        checked = 0
        for revision in revisions:
            if args.ref:
                listing = ['ls-tree', '-r', '--name-only', '-z', revision, '--']
            elif revision:
                # --root covers root commits; -m inspects merge results against
                # each parent so private merge resolutions cannot evade the gate.
                listing = ['diff-tree', '--root', '-m', '--no-commit-id', '--name-only',
                           '-z', '--diff-filter=ACMRT', '-r', revision, '--']
            else:
                listing = ['diff', '--cached', '--name-only', '-z', '--diff-filter=ACMRT', base, '--']
            paths = sorted(set(path for path in git(*listing).decode().split('\0') if path))
            for path in paths:
                checked += 1
                data = git('show', (revision or '') + ':' + path)
                errors.extend(inspect_version(path, data))
        if errors:
            raise CheckError('\n'.join(sorted(set(errors))))
    except (CheckError, UnicodeError, ValueError) as error:
        # Never echo Git's captured stderr or the inspected file contents.
        raise SystemExit(str(error) if isinstance(error, CheckError) else
                         'Repository inspection failed; no files were approved.') from None

    scope = 'snapshot' if args.ref else 'changed'
    print(f'Private-code gate passed: {checked} {scope} file versions across {len(revisions)} revision(s).')


if __name__ == '__main__':
    main()
