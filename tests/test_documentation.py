"""Check public documentation links, catalogs and optionally live discovery."""
import argparse,json,re,urllib.request
from pathlib import Path

root=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--live',action='store_true')
args=parser.parse_args()
reference=json.loads((root/'docs/reference/api-reference.json').read_text(encoding='utf-8'))
guide=json.loads((root/'docs/reference/mcp-tool-guide.json').read_text(encoding='utf-8'))
operations=[op for group in reference['groups'] for op in group['operations']]
normalize=lambda s:re.sub(r'\{\w+\}','{id}',s.lower())
assert len(operations)==57 and len(guide)==38
assert reference['verifiedOn']==reference['examplesVerifiedOn']=='October 17, 2026'
assert len({normalize(op['route']) for op in operations})==len(operations)
assert all(op['method']=='GET' and op['isPublic'] for op in operations)
assert all(tool['name'] in (root/'docs/MCP.md').read_text(encoding='utf-8') for tool in guide)
for op in operations:
    for response in op['responses']:
        if response.get('example'):json.loads(response['example'])
errors=[];links=0;docs=list(root.rglob('*.md'))
for path in docs:
    if any(part in {'.git','bin','obj','node_modules'} for part in path.relative_to(root).parts):continue
    text=path.read_text(encoding='utf-8-sig')
    text=re.sub(r'(?ms)^```.*?^```[^\n]*','',text)
    for match in re.finditer(r'\]\(([^\s)]+)(?:\s+"[^"]*")?\)',text):
        target=match[1].strip('<>').split('#')[0].split('?')[0]
        if not target or re.match(r'^[a-zA-Z][a-zA-Z0-9+.-]*:',target) or target.startswith('/'):continue
        links+=1
        if not(path.parent/target).exists():errors.append(str(path.relative_to(root))+': '+target)
assert not errors,errors
for path in [root/'README.md',root/'llms.txt',*list((root/'docs').glob('*.md'))]:
    assert not re.search(r'(?:currently has 25 tools|18 tools total|33 current)',path.read_text(encoding='utf-8')),path
if args.live:
    def request(path,payload=None):
        headers={'User-Agent':'AtlasPublicDocsCheck/1.0','Accept':'application/json, text/event-stream'}
        if payload is not None:headers['Content-Type']='application/json'
        with urllib.request.urlopen(urllib.request.Request('https://api.blackportal.cloud'+path,data=json.dumps(payload).encode() if payload else None,headers=headers),timeout=60) as response:raw=response.read().decode()
        if raw.startswith(('event:','data:')):raw=next(line[6:] for line in raw.splitlines() if line.startswith('data: '))
        return json.loads(raw)
    schema=request('/openapi/v1.json');published={normalize(p):o['get'] for p,o in schema['paths'].items()}
    assert set(published)=={normalize(op['route']) for op in operations}
    for op in operations:
        actual={(p['in'],p['name'],bool(p.get('required'))) for p in published[normalize(op['route'])].get('parameters',[])}
        documented={(p['in'],p['name'],p['required']) for p in op['parameters']}
        assert actual<=documented,(op['route'],actual-documented)
        allowed={('query','sha256',False),('header','Range',False)} if op['route'].endswith('world-download.zip') else set()
        assert documented-actual<=allowed,(op['route'],documented-actual)
    tools=request('/mcp',{'jsonrpc':'2.0','id':1,'method':'tools/list','params':{}})['result']['tools']
    assert {tool['name'] for tool in tools}=={tool['name'] for tool in guide}
print(f'PASS: {len(operations)} public routes, {len(guide)} MCP tools, {links} local links; live discovery checked: {args.live}.')
