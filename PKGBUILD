pkgname=rhi-linux
pkgver=0.2.1
pkgrel=1
pkgdesc="Steam Proton mod deployment manager for AMD GPUs"
arch=('x86_64')
url="https://github.com/winterofhell/rhi-linux"
license=('GPL-3.0-only')
depends=('dotnet-runtime-8.0' 'fontconfig' 'libarchive')
makedepends=('dotnet-sdk-8.0')
source=()
sha256sums=()

build() {
  cd "$startdir"
  dotnet restore RhiLinux.sln -r linux-x64
  dotnet publish src/RhiLinux.Cli/RhiLinux.Cli.csproj -c Release -r linux-x64 --no-restore --no-self-contained -m:1 /nodeReuse:false /p:UseSharedCompilation=false -o "$srcdir/publish/cli"
  dotnet publish src/RhiLinux.Gui/RhiLinux.Gui.csproj -c Release -r linux-x64 --no-restore --no-self-contained -m:1 /nodeReuse:false /p:UseSharedCompilation=false -o "$srcdir/publish/gui"
}

check() {
  cd "$startdir"
  dotnet test tests/RhiLinux.Tests/RhiLinux.Tests.csproj -c Release --no-restore
}

package() {
  cd "$startdir"
  install -d "$pkgdir/usr/lib/rhi-linux/cli" "$pkgdir/usr/lib/rhi-linux/gui" "$pkgdir/usr/bin" "$pkgdir/usr/share/applications"
  cp -a "$srcdir/publish/cli/." "$pkgdir/usr/lib/rhi-linux/cli/"
  cp -a "$srcdir/publish/gui/." "$pkgdir/usr/lib/rhi-linux/gui/"
  install -Dm755 packaging/rhi-linux "$pkgdir/usr/bin/rhi-linux"
  install -Dm755 packaging/rhi-linux-gui "$pkgdir/usr/bin/rhi-linux-gui"
  install -Dm644 packaging/io.github.rhilinux.RhiLinux.desktop "$pkgdir/usr/share/applications/io.github.rhilinux.RhiLinux.desktop"
  install -Dm644 LICENSE "$pkgdir/usr/share/licenses/$pkgname/LICENSE"
  install -Dm644 THIRD_PARTY_NOTICES.md "$pkgdir/usr/share/doc/$pkgname/THIRD_PARTY_NOTICES.md"
}
