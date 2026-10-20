# Fork: lists texts of the default .resx files that the Korean (.ko-KR.resx) ones lack (MISSING) or
# kept in English (SAME: same as the default and has 3+ Latin letters), for the Korean screens.
#
# python tools/missing_ko.py
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET

sys.stdout.reconfigure(encoding='utf-8')
root = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))

def load(p):
    d = {}
    if not os.path.exists(p):
        return d
    for data in ET.parse(p).getroot().findall('data'):
        name = data.get('name')
        typ = data.get('type')
        v = data.find('value')
        if v is None or typ is not None:
            continue
        d[name] = v.text or ''
    return d

def wanted(name, value):
    if name.startswith('>>') or name.startswith('$'):
        return False
    if '.' in name:  # designer property
        return name.endswith('.Text') or name.endswith('.ToolTip') or name.endswith('ToolTip')
    return True

total = 0
for base in sorted(glob.glob(os.path.join(root, '**', '*.resx'), recursive=True)):
    if os.sep + 'out' + os.sep in base or re.search(r'\.[a-z]{2}(-[A-Z]{2})?\.resx$', base):
        continue
    ko = base[:-5] + '.ko-KR.resx'
    b = load(base) or {}
    k = load(ko) or {}
    rel = os.path.relpath(base, root)
    rows = []
    for name, value in b.items():
        if not wanted(name, value) or not value.strip():
            continue
        if name not in k:
            rows.append(('MISSING', name, value))
        elif k[name] == value and re.search('[A-Za-z]{3}', value):
            rows.append(('SAME   ', name, value))
    if rows:
        print(f'== {rel} (ko-KR {"exists" if os.path.exists(ko) else "NONE"})')
        for r in rows:
            print(f'  {r[0]} {r[1]}: {r[2][:90]!r}')
        total += len(rows)
print('total', total)
