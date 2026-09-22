#!/bin/zsh
# Sync sources to a Windows build machine over SSH, publish, and build the installer.
#   ./build.sh          publish + installer
#   ./build.sh --sync   only copy sources
#
# Machine settings live in build.local (not committed):
#   BUILD_HOST=user@host        SSH target (Windows with OpenSSH, .NET 10 SDK, Inno Setup 6)
#   BUILD_KEY=~/.ssh/key        private key for that host
#   BUILD_DIR='C:\path\to\repo' working copy on the Windows side
set -e
cd "$(dirname "$0")"
[[ -f build.local ]] || { echo "build.local is missing, see build.sh header"; exit 1; }
source build.local
: "${BUILD_HOST:?}" "${BUILD_KEY:?}" "${BUILD_DIR:?}"
SSH=(ssh -i "$BUILD_KEY" "$BUILD_HOST")

# rsync is not on the Windows side; stream a tarball instead (Windows ships bsdtar).
"${SSH[@]}" "if not exist $BUILD_DIR mkdir $BUILD_DIR"
COPYFILE_DISABLE=1 tar czf - --exclude bin --exclude obj --exclude dist --exclude .git --exclude build.local --exclude '._*' --exclude .DS_Store . \
  | "${SSH[@]}" "cd /d $BUILD_DIR && tar xzf -"
[[ "$1" == "--sync" ]] && exit 0

"${SSH[@]}" "taskkill /im GigaPisar.exe /f >nul 2>&1 & cd /d $BUILD_DIR\\src && dotnet publish -c Release -r win-x64 --self-contained true -o ..\\dist\\app -nologo -v q" | LC_ALL=C tr -d '\r'
"${SSH[@]}" "cd /d $BUILD_DIR\\installer && \"%LOCALAPPDATA%\\Programs\\Inno Setup 6\\ISCC.exe\" /Q setup.iss && dir ..\\dist\\*.exe" | LC_ALL=C tr -d '\r'
