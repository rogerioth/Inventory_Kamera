import json
import subprocess
import urllib.request

TOKEN = 'ae51f2ff5bd52fc3cd6e3169d606cb25eedc2e4d'
P = 'rogerioth_Inventory_Kamera'
HEADERS = {'Authorization': f'Bearer {TOKEN}'}


def get(path, **params):
    qs = '&'.join(f'{k}={v}' for k, v in params.items())
    req = urllib.request.Request(f'https://sonarcloud.io/api/{path}?{qs}', headers=HEADERS)
    with urllib.request.urlopen(req) as r:
        return json.load(r)


files = subprocess.run(['git', 'diff', '--name-only', 'origin/master...HEAD'],
                       capture_output=True, text=True, cwd='C:/Users/Rogerio/git/Inventory_Kamera').stdout.split()

components = {c['key']: c for c in get('components/show', component=P + ':' + files[0]) and []} if False else None

# resolve all components once
comps = get('components/search_projects', organization='rogerioth')  # not needed; build refs map from responses

seen_files = set()
for f in files:
    key = f'{P}:{f}'
    try:
        d = get('duplications/show', key=key, pullRequest='33')
    except Exception:
        continue
    dups = d.get('duplications', [])
    if not dups:
        continue
    refs = d.get('components', {})
    print(f'== {f}')
    for dup in dups:
        blocks = dup.get('blocks', [])
        desc = []
        for b in blocks:
            ck = refs.get(str(b.get('_ref')), {}).get('key', '?')
            desc.append(f"{ck.split(':')[-1]}:{b['from']}+{b['size']}")
        print('   ', ' <-> '.join(desc))
