#!/usr/bin/env python3
"""Build the unsigned native service package without Hue Bridge credentials."""
import os
import tempfile
import zipfile
from pathlib import Path

root = Path(__file__).resolve().parent
runtime = root / 'deployment'
target = root / 'HueEngine-unsigned.tpk'
manifest = '''<?xml version="1.0" encoding="utf-8"?>
<manifest package="HueEngine1" version="0.2.0" api-version="6.5" xmlns="http://tizen.org/ns/packages">
  <author>Local developer</author><profile name="tv"/>
  <service-application appid="HueEngine1.service" exec="HueTvSync.dll" multiple="false" nodisplay="true" taskmanage="false" api-version="9" type="dotnet">
    <label>画面灯光同步核心</label><icon>icon.png</icon>
  </service-application>
  <privileges><privilege>http://tizen.org/privilege/internet</privilege></privileges>
</manifest>'''
names = ('HueTvSync.dll', 'HueTvSync.deps.json', 'HueTvSync.runtimeconfig.json', 'BouncyCastle.Cryptography.dll')
if not all((runtime/name).is_file() for name in names):
    raise SystemExit('A required native service file is missing')
fd, temp = tempfile.mkstemp(prefix='.tpk-', dir=str(root))
try:
    os.fchmod(fd, 0o600)
    with os.fdopen(fd, 'w+b') as out:
        with zipfile.ZipFile(out, 'w', compression=zipfile.ZIP_DEFLATED) as package:
            package.writestr('tizen-manifest.xml', manifest)
            for name in names:
                package.write(runtime/name, 'bin/'+name)
            package.write(root/'tizen-web'/'icon.png', 'res/icon.png')
        out.flush();os.fsync(out.fileno())
    os.replace(temp, target)
    print('Prepared unsigned native service package:', target)
except BaseException:
    try: os.unlink(temp)
    except FileNotFoundError: pass
    raise
