import os, pathlib, re, sys, unicodedata

ROOT = str(pathlib.Path(__file__).resolve().parent.parent)
md_files = []
for dp, dn, fn in os.walk(ROOT):
    # Normalised so the exclusions below hold on Windows too, where os.walk
    # yields backslashes and a '/'-only test would silently stop excluding.
    norm = dp.replace(os.sep, "/") + "/"
    # external/ is excluded: vendored content there (today the committed
    # package feed, ADR-0021) is not held to this repository's documentation
    # conventions. Links *to* files under external/ still resolve normally.
    if ".git" in norm or "/bin/" in norm or "/obj/" in norm or "/external/" in norm: continue
    for f in fn:
        if f.endswith(".md"): md_files.append(os.path.join(dp, f))

def slug(t):
    # GitHub-style anchor slug
    t = t.strip()
    t = re.sub(r'`([^`]*)`', r'\1', t)
    t = re.sub(r'\*\*([^*]*)\*\*', r'\1', t)
    t = re.sub(r'\*([^*]*)\*', r'\1', t)
    t = re.sub(r'\[([^\]]*)\]\([^)]*\)', r'\1', t)
    t = t.lower()
    out = []
    for ch in t:
        if ch.isalnum() or ch in ('-', '_'):
            out.append(ch)
        elif ch in (' ', '\t'):
            out.append('-')
        elif unicodedata.category(ch).startswith('M'):
            out.append(ch)
        # everything else dropped
    return ''.join(out)

anchors = {}
for p in md_files:
    s = set()
    incode = False
    for line in open(p, encoding='utf-8'):
        if line.lstrip().startswith("```"):
            incode = not incode; continue
        if incode: continue
        m = re.match(r'^(#{1,6})\s+(.*?)\s*$', line)
        if m:
            base = slug(m.group(2))
            a = base; n = 1
            while a in s:
                a = f"{base}-{n}"; n += 1
            s.add(a)
    anchors[os.path.realpath(p)] = s

LINK = re.compile(r'\[[^\]]*\]\(([^)\s]+)\)')
bad = []
checked = 0
for p in md_files:
    src = open(p, encoding='utf-8').read()
    # strip fenced code blocks
    src = re.sub(r'```.*?```', '', src, flags=re.S)
    for target in LINK.findall(src):
        if target.startswith(('http://', 'https://', 'mailto:')): continue
        checked += 1
        if target.startswith('#'):
            fpath = os.path.realpath(p); frag = target[1:]
        else:
            fp, _, frag = target.partition('#')
            fpath = os.path.realpath(os.path.join(os.path.dirname(p), fp))
            if not os.path.exists(fpath):
                bad.append((p, target, "missing file")); continue
        if frag:
            if fpath not in anchors:
                bad.append((p, target, "target not markdown")); continue
            if frag not in anchors[fpath]:
                bad.append((p, target, "missing anchor"))

print(f"files={len(md_files)} relative_links_checked={checked} broken={len(bad)}")
for p, t, why in bad:
    print(f"  {os.path.relpath(p, ROOT)} -> {t}   [{why}]")
sys.exit(1 if bad else 0)
