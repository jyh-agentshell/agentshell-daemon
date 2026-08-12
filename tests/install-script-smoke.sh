#!/usr/bin/env bash
# install.sh 首次安装与重复安装失败关闭的隔离回归。
set -euo pipefail

ROOT=$(mktemp -d)
trap 'rm -rf -- "$ROOT"' EXIT
BIN="$ROOT/mock-bin"
INSTALL="$ROOT/install"
CONFIG="$ROOT/config"
SYSTEMD="$ROOT/systemd"
mkdir -p "$BIN" "$INSTALL" "$CONFIG" "$SYSTEMD"

cat > "$BIN/curl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
target="${@: -1}"
if [[ "$*" == *"releases/latest"* ]]; then
  printf '{"tag_name":"v0.0.0-test"}\n'
elif [[ "$target" == *"checksum"* ]]; then
  printf '%s  agentshell-daemon\n' "$(printf '%s\n' '#!/bin/sh' 'test "$1" = --generate-config && printf "[daemon]\\n"' | sha256sum | cut -d' ' -f1)" > "$target"
else
  printf '%s\n' '#!/bin/sh' 'test "$1" = --generate-config && printf "[daemon]\\n"' > "$target"
fi
EOF
cat > "$BIN/systemctl" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
cat > "$BIN/openssl" <<'EOF'
#!/usr/bin/env bash
if [ "$1" = genpkey ]; then printf private > "${@: -1}"; else printf public; fi
EOF
chmod +x "$BIN/curl" "$BIN/systemctl" "$BIN/openssl"

PATH="$BIN:$PATH" \
AGENTSHELL_SKIP_ROOT_CHECK=1 \
AGENTSHELL_INSTALL_DIR="$INSTALL" \
AGENTSHELL_CONFIG_DIR="$CONFIG" \
AGENTSHELL_SERVICE_NAME="agentshell-test" \
AGENTSHELL_SYSTEMD_DIR="$SYSTEMD" \
SUDO_USER="root" \
bash ./install.sh

test -x "$INSTALL/agentshell-daemon"
before=$(sha256sum "$INSTALL/agentshell-daemon" | cut -d' ' -f1)
set +e
PATH="$BIN:$PATH" AGENTSHELL_SKIP_ROOT_CHECK=1 AGENTSHELL_INSTALL_DIR="$INSTALL" AGENTSHELL_CONFIG_DIR="$CONFIG" SUDO_USER="root" bash ./install.sh >/dev/null 2>&1
status=$?
set -e
after=$(sha256sum "$INSTALL/agentshell-daemon" | cut -d' ' -f1)
test "$status" -eq 2
test "$before" = "$after"
