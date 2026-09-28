"""Package a signed update and a first-install ZIP. Never includes the signing key."""
from pathlib import Path
import zipfile, hashlib, shutil, subprocess, argparse, os, xml.etree.ElementTree as ET
p=argparse.ArgumentParser()
p.add_argument('--dotnet',default='dotnet')
p.add_argument('--key',required=True)
p.add_argument('--signtool',default='signtool.exe')
p.add_argument('--repository',default='masanobumanno-netizen/GonioAPP')
a=p.parse_args()
root=Path(__file__).resolve().parents[1]
version=ET.parse(root/'apps/device-bridge/DeviceBridge.csproj').findtext('./PropertyGroup/Version')
publish=root/'dist/GonioWeb-Windows-x64'
# A signed update manifest is not a Windows code signature.
# Verify the exact binaries before constructing either distribution ZIP.
if os.name != 'nt':
 raise SystemExit('Release packaging requires Windows Authenticode verification. See docs/WINDOWS-SIGNING.md; unsigned release packaging is disabled.')
subprocess.run(['powershell.exe','-NoProfile','-File',str(root/'scripts/sign-windows.ps1'),'-PublishDirectory',str(publish),'-SignTool',a.signtool,'-VerifyOnly'],check=True)
assert (publish/'GonioWeb.exe').read_bytes()[:2]==b'MZ'
assert (publish/'launcher/GonioLauncher.exe').read_bytes()[:2]==b'MZ'
(publish/'version.txt').write_text(version+'\n')
for name,target in [('Start-Gonio.cmd','Start-Gonio.cmd'),('Install-Gonio.cmd','Install-Gonio.cmd'),('README-Windows.txt','はじめに.txt')]:
 data=(root/'packaging/windows'/name).read_text()
 (publish/target).write_bytes(data.replace('\r\n','\n').replace('\n','\r\n').encode('utf-8-sig' if target.endswith('.txt') else 'ascii'))
update=root/f'dist/GonioWeb-update-{version}.zip'
with zipfile.ZipFile(update,'w',zipfile.ZIP_DEFLATED) as z:
 for f in sorted(publish.rglob('*')):
  rel=f.relative_to(publish)
  if f.is_file() and rel.parts[0] not in ('launcher','update-config.json','Install-Gonio.cmd'):
   z.write(f,rel)
manifest=root/'dist/update-manifest.json'
config=publish/'update-config.json'
subprocess.run([a.dotnet,str(root/'apps/launcher/bin/Debug/net8.0/GonioLauncher.dll'),'--sign',str(Path(a.key).resolve()),a.repository,version,str(update),str(manifest),str(config)],check=True)
# Public configuration is safe to commit; never store the private key here.
shutil.copyfile(config,root/'packaging/windows/update-config.json')
archive=root/f'dist/GonioWeb-Windows-x64-v{version}.zip'
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
 for f in sorted(publish.rglob('*')):
  if f.is_file():z.write(f,Path('GonioWeb-Windows-x64')/f.relative_to(publish))
for file in [archive,update]:
 digest=hashlib.sha256(file.read_bytes()).hexdigest()
 file.with_suffix('.zip.sha256').write_text(digest+'  '+file.name+'\n')
 print(f'{file.name}: {file.stat().st_size/1024/1024:.1f} MB; SHA256 {digest}')
