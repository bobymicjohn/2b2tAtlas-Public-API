"""Authorized non-destructive live checks. Tokens are short-lived and never printed/saved."""
import base64, datetime, hashlib, hmac, json, os, sqlite3, time, urllib.request, urllib.error

key = os.environ['JwtSettings__SecretKey']
issuer = os.environ.get('JwtSettings__Issuer', '2b2tAtlas')
audience = os.environ.get('JwtSettings__Audience', '2b2tAtlas')
db = sqlite3.connect('file:C:/AtlasApi/data/atlas.db?mode=ro', uri=True)
users = db.execute('SELECT Id,Username,PasswordHash FROM Users WHERE Id IN (1,2,3)').fetchall()
counts_before = {t: db.execute(f'SELECT COUNT(*) FROM {t}').fetchone()[0] for t in ['Locations','Renders','Attachments','Warps','Users']}
def b64(data): return base64.urlsafe_b64encode(data).rstrip(b'=').decode()
def token(user):
    uid,name,digest=user
    stamp=hmac.new(key.encode(), f'atlas-session-v1:{uid}:{digest}'.encode(), hashlib.sha256).hexdigest().upper()
    claims={'iss':issuer,'aud':audience,'exp':int(time.time())+120,'nbf':int(time.time())-10,
            'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier':str(uid),'atlas_session':stamp,
            'superadmin':'true','atlas_owner':'true','perm':['locations.delete','roles.manage']}
    body=b64(json.dumps({'alg':'HS256','typ':'JWT'}).encode())+'.'+b64(json.dumps(claims).encode())
    return body+'.'+b64(hmac.new(key.encode(),body.encode(),hashlib.sha256).digest())
def request(base, route, method, bearer, body=None):
    headers={'User-Agent':'AtlasRecoveryValidation/1.0','Authorization':'Bearer '+bearer}
    if body is not None: headers['Content-Type']='application/json'
    req=urllib.request.Request(base+route,data=None if body is None else json.dumps(body).encode(),method=method,headers=headers)
    try:
        with urllib.request.urlopen(req,timeout=30) as response: return response.status
    except urllib.error.HTTPError as error: return error.code
results=[]
for base in ['http://127.0.0.1:5297','http://127.0.0.1:5297']:
    for user in users:
        bearer=token(user)
        profile=request(base,'/api/auth/profile','GET',bearer)
        assert profile==200,(user[1],profile)
        health=request(base,'/api/admin/recovery','GET',bearer)
        assert health==(200 if user[0]==1 else 403),(user[1],'recovery health',health)
        results.append(dict(base=base,user=user[1],method='GET',path='/api/admin/recovery',status=health))
        if user[0]!=1:
            for method,path,body in [('DELETE','/api/groups/-2147483648',None),('PUT','/api/admin/users/-2147483648',{}),
                    ('PUT','/api/maprenders',{}),('POST','/api/enrichment/run',{'autoApply':True}),
                    ('POST','/api/ingestion-jobs/upload-sessions',{})]:
                code=request(base,path,method,bearer,body)
                assert code==403,(user[1],path,code)
                results.append(dict(base=base,user=user[1],method=method,path=path,status=code))
        else:
            # A negative nonexistent group cannot be removed. The admitted request
            # nevertheless proves the real checkpoint succeeds before controller execution.
            code=request(base,'/api/groups/-2147483648','DELETE',bearer)
            assert code==404,('owner checkpoint',code)
            results.append(dict(base=base,user=user[1],method='DELETE',path='/api/groups/-2147483648',status=code))
counts_after = {t: db.execute(f'SELECT COUNT(*) FROM {t}').fetchone()[0] for t in counts_before}
assert all(counts_after[t]>=counts_before[t] for t in counts_before),(counts_before,counts_after)
print(json.dumps({'checkedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'checks':results,
                  'countsBefore':counts_before,'countsAfter':counts_after},indent=2))
