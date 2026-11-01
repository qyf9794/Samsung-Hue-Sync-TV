#!/usr/bin/env python3
"""Build a private, unsigned development WGT without Hue Bridge credentials."""
import json
import os
import secrets
import re
import tempfile
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parent
web = root / 'tizen-web'
target = root / 'HueTvSync-unsigned.wgt'
members = {
    'config.xml': web / 'config.xml',
    'index.html': web / 'index.html',
    'app.js': web / 'app.js',
    'style.css': web / 'style.css',
    'icon.png': web / 'icon.png',
    'service/index.js': web / 'service' / 'index.js',
    'service/pairing.js': web / 'service' / 'pairing.js',
}
if not all(path.is_file() for path in members.values()):
    raise SystemExit('A required application file is missing')
# Keep the installed UI's token when rebuilding an existing package.
if target.exists():
    with zipfile.ZipFile(target) as previous:
        token = json.loads(previous.read('control-token.json'))['token']
    if not isinstance(token, str) or not re.fullmatch(r'[0-9a-fA-F]{64}', token):
        raise SystemExit('Existing package has an invalid control token')
else:
    token = secrets.token_hex(32)
fd, temp = tempfile.mkstemp(prefix='.wgt-', dir=str(root))
try:
    os.fchmod(fd, 0o600)
    with os.fdopen(fd, 'w+b') as out:
        with zipfile.ZipFile(out, 'w', compression=zipfile.ZIP_DEFLATED) as package:
            for name, path in members.items():
                package.write(path, name)
            package.writestr('control-token.json', json.dumps({'token': token}))
            package.writestr('control-token.js', 'window.HUE_CONTROL_TOKEN=' + json.dumps(token) + ';\n')
        out.flush()
        os.fsync(out.fileno())
    os.replace(temp, target)
    print('Prepared unsigned development package:', target)
except BaseException:
    try:
        os.unlink(temp)
    except FileNotFoundError:
        pass
    raise
