"""Inspect staged or committed public changes without printing sensitive values."""
import argparse,re,subprocess
from pathlib import Path

root=Path(__file__).resolve().parents[1]
def git(*args):return subprocess.check_output(['git',*args],cwd=root)
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--base',default='origin/main')
parser.add_argument('--head',help='Commit to inspect, otherwise inspect the index')
args=parser.parse_args()
revisions=git('rev-list','--reverse',args.base+'..'+args.head).decode().splitlines() if args.head else [None]
errors=[];checked=0
private_path=re.compile(r'(?i)(?:^|/)(?:commerce|payments?)(?:/|\.)|(?:^|/)(?:Shop|Checkout|Advertising|Stripe|Payment)[^/]*\.(?:cs|razor|js|json|sql)$')
secret_patterns=[r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',r'\bgh[pousr]_[A-Za-z0-9]{25,}',r'\bgithub_pat_[A-Za-z0-9_]{35,}',r'\beyJ[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}',r'(?i)[A-Z]:[\\/]+Users[\\/]+(?!atlas-operator\b|example\b)[^\\/\s"\']+']
for revision in revisions:
    diff=['diff-tree','--no-commit-id','--name-only','--diff-filter=ACMRT','-r',revision] if revision else ['diff','--cached','--name-only','--diff-filter=ACMRT',args.base]
    for path in git(*diff).decode().splitlines():
        checked+=1
        if private_path.search(path):errors.append('Private feature path: '+path)
        if re.search(r'(?i)\.(?:db|sqlite3?|pfx|p12|key|env)$',path):errors.append('Private/runtime material: '+path)
        data=git('show',(revision or '')+':'+path)
        try:text=data.decode('utf-8-sig')
        except UnicodeDecodeError:continue
        if any(re.search(pattern,text) for pattern in secret_patterns):errors.append('Inspect possible private content: '+path)
        if path.endswith(('.cs','.razor','.csproj','.js')) and re.search(r'\b(?:ShopListing|ShopService|PaymentService|StripeClient|CheckoutSession)\b',text):errors.append('Private feature wiring: '+path)
if errors:raise SystemExit('\n'.join(sorted(set(errors))))
print(f'Private-code gate passed: {checked} changed file versions across {len(revisions)} revision(s).')
