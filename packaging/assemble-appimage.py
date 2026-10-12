#!/usr/bin/env python3
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('--gui', type=Path, required=True)
parser.add_argument('--cli', type=Path, required=True)
parser.add_argument('--runtime', type=Path, required=True)
parser.add_argument('--runtime-sha256', required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--appdir', type=Path, required=True)
parser.add_argument('--patchelf', default='patchelf')
parser.add_argument('--mksquashfs', default='mksquashfs')
parser.add_argument('--version', default='0.4.0')
parser.add_argument('--library-directory', type=Path, action='append', default=[])
args = parser.parse_args()
root = Path(__file__).resolve().parent.parent
assert hashlib.sha256(args.runtime.read_bytes()).hexdigest() == args.runtime_sha256, 'AppImage runtime hash mismatch'
assert not args.appdir.exists(), 'Use a fresh AppDir'
assert not args.output.exists(), 'Refusing to overwrite an existing AppImage'

def elf(path):
    try:
        with path.open('rb') as stream:
            header = stream.read(20)
        return len(header) == 20 and header[:5] == b'\x7fELF\x02' and header[18:20] == b'\x3e\x00'
    except OSError:
        return False

def run(*command):
    return subprocess.check_output(command, text=True, stderr=subprocess.DEVNULL).strip()

app = args.appdir / 'usr/lib/rhi-linux'
app.mkdir(parents=True)
for source in (args.gui, args.cli):
    for path in source.iterdir():
        target = app / path.name
        if target.exists():
            assert target.is_file() and path.is_file() and target.read_bytes() == path.read_bytes(), f'Publish output conflict: {path.name}'
        elif path.is_dir():
            shutil.copytree(path, target)
        else:
            shutil.copy2(path, target)
native = app / 'native'
native.mkdir()
sources = {}
search = args.library_directory + [Path('/usr/lib/x86_64-linux-gnu'), Path('/lib/x86_64-linux-gnu'), Path('/usr/lib64'), Path('/usr/lib')]
if Path('/nix/store').exists():
    search += sorted(Path('/nix/store').glob('*/lib'))

def locate(name, preferred=()):
    for directory in list(preferred) + search:
        path = directory / name
        if elf(path):
            return path.resolve()
    raise RuntimeError(f'Native dependency unavailable: {name}')

queue = []
def add(name, source):
    if name in sources:
        return
    if (app / name).exists():
        return
    assert elf(source), source
    shutil.copy2(source, native / name)
    sources[name] = {'source': str(source), 'sha256': hashlib.sha256(source.read_bytes()).hexdigest()}
    queue.append(source)

for library in ('libfontconfig.so.1', 'libfreetype.so.6', 'libX11.so.6', 'libXext.so.6', 'libXi.so.6', 'libXrandr.so.2', 'libXcursor.so.1', 'libXrender.so.1', 'libxcb.so.1', 'libICE.so.6', 'libSM.so.6', 'libGL.so.1', 'libGLX.so.0', 'libOpenGL.so.0', 'libssl.so.3', 'libcrypto.so.3', 'libresolv.so.2'):
    add(library, locate(library))
queue += [path for path in app.rglob('*') if path.is_file() and native not in path.parents and elf(path)]
seen = set()
while queue:
    source = queue.pop()
    if source in seen:
        continue
    seen.add(source)
    try:
        interpreter = run(args.patchelf, '--print-interpreter', str(source))
        if interpreter:
            add('ld-linux-x86-64.so.2', Path(interpreter).resolve())
    except subprocess.CalledProcessError:
        pass
    needed = run(args.patchelf, '--print-needed', str(source)).splitlines()
    rpath = run(args.patchelf, '--print-rpath', str(source))
    preferred = [Path(part.replace('${ORIGIN}', str(source.parent)).replace('$ORIGIN', str(source.parent))) for part in rpath.split(':') if part]
    output = subprocess.run(['ldd', str(source)], capture_output=True, text=True)
    resolved = {match.group(1): Path(match.group(2)) for match in re.finditer(r'^\s*(\S+)\s+=>\s+(/\S+)', output.stdout, re.M)}
    for name in needed:
        if '/' in name:
            dependency = Path(name)
            soname = run(args.patchelf, '--print-soname', str(dependency)) or dependency.name
            add(soname, dependency.resolve())
        else:
            add(name, resolved.get(name) or locate(name, preferred))
assert (native / 'ld-linux-x86-64.so.2').exists()
for path in app.rglob('*'):
    if not path.is_file() or not elf(path):
        continue
    path.chmod(path.stat().st_mode | 0o200)
    for name in run(args.patchelf, '--print-needed', str(path)).splitlines():
        if '/' in name:
            replacement = run(args.patchelf, '--print-soname', name) or Path(name).name
            subprocess.run([args.patchelf, '--replace-needed', name, replacement, str(path)], check=True)
    if path.name != 'ld-linux-x86-64.so.2':
        subprocess.run([args.patchelf, '--set-rpath', '$ORIGIN' if native in path.parents else '$ORIGIN/native:$ORIGIN', str(path)], check=True)
    result = subprocess.run([args.patchelf, '--print-interpreter', str(path)], capture_output=True, text=True)
    if result.returncode == 0 and result.stdout.strip():
        subprocess.run([args.patchelf, '--set-interpreter', '/lib64/ld-linux-x86-64.so.2', str(path)], check=True)
shutil.move(native / 'ld-linux-x86-64.so.2', app / 'ld-linux-x86-64.so.2')
shutil.copy2(root / 'packaging/AppRun', args.appdir / 'AppRun')
(args.appdir / 'AppRun').chmod(0o755)
icon = 'io.github.rhilinux.RhiLinux.png'
shutil.copy2(root / 'packaging' / icon, args.appdir / icon)
(args.appdir / '.DirIcon').symlink_to(icon)
desktop = (root / 'packaging/io.github.rhilinux.RhiLinux.desktop').read_text().replace('Exec=rhi-linux-gui', 'Exec=AppRun')
(args.appdir / 'io.github.rhilinux.RhiLinux.desktop').write_text(desktop)
legal = args.appdir / 'usr/share/licenses/rhi-linux'
legal.mkdir(parents=True)
for filename in ('LICENSE', 'THIRD_PARTY_NOTICES.md'):
    shutil.copy2(root / filename, legal / filename)
version = args.appdir / 'usr/share/rhi-linux/version'
version.parent.mkdir(parents=True)
version.write_text(f'RHI Linux {args.version}\n')
shutil.copy2(root / 'packaging/fonts.conf', version.parent / 'fonts.conf')
for project in ('RhiLinux.Gui', 'RhiLinux.Cli'):
    assets_path = root / 'src' / project / 'obj/project.assets.json'
    assets = json.loads(assets_path.read_text())
    for info in assets['libraries'].values():
        if info['type'] != 'package':
            continue
        for folder in assets['packageFolders']:
            source = Path(folder) / info['path']
            if not source.exists():
                continue
            for path in source.rglob('*'):
                if path.is_file() and path.name.lower().startswith(('license', 'copying', 'third-party', 'third_party', 'notice')):
                    target = legal.parent / 'nuget' / info['path'] / path.relative_to(source)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(path, target)
manifest = {'version': args.version, 'architecture': 'x86_64', 'runtimeSha256': args.runtime_sha256, 'nativeLibraries': sources}
(version.parent / 'package-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
for path in app.rglob('*'):
    if path.is_file() and elf(path):
        assert '/nix/store/' not in run(args.patchelf, '--print-rpath', str(path)), path
        assert all('/' not in name for name in run(args.patchelf, '--print-needed', str(path)).splitlines()), path
        result = subprocess.run([args.patchelf, '--print-interpreter', str(path)], capture_output=True, text=True)
        assert '/nix/store/' not in result.stdout, path
args.output.parent.mkdir(parents=True, exist_ok=True)
squashfs = args.output.with_suffix('.squashfs')
assert not squashfs.exists()
subprocess.run([args.mksquashfs, str(args.appdir), str(squashfs), '-noappend', '-comp', 'gzip', '-all-root', '-no-progress', '-processors', '2'], check=True)
with args.output.open('wb') as target:
    target.write(args.runtime.read_bytes())
    with squashfs.open('rb') as payload:
        shutil.copyfileobj(payload, target)
squashfs.unlink()
args.output.chmod(0o755)
digest = hashlib.sha256(args.output.read_bytes()).hexdigest()
args.output.with_suffix(args.output.suffix + '.sha256').write_text(f'{digest}  {args.output.name}\n')
print(f'Created {args.output}: {args.output.stat().st_size} bytes; {len(sources)} native library names; SHA-256 {digest}')
