#!/usr/bin/env bash
# AgentShell 守护进程一键安装脚本
# 用法见 README：先下载并人工校验脚本，再以 sudo 执行。
#
# 此脚本做三件事：
# 1. 从 GitHub Releases 下载最新二进制
# 2. SHA256 校验
# 3. 写入 systemd service 并启动

set -euo pipefail

# ─── 配置 ───────────────────────────────────────────────
REPO="jyh-agentshell/agentshell-daemon"
INSTALL_DIR="${AGENTSHELL_INSTALL_DIR:-/usr/local/bin}"
BINARY_NAME="agentshell-daemon"
SERVICE_NAME="${AGENTSHELL_SERVICE_NAME:-agentshell-daemon}"
SYSTEMD_DIR="${AGENTSHELL_SYSTEMD_DIR:-/etc/systemd/system}"

# 颜色输出
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log_info()  { echo -e "${GREEN}[+]${NC} $1"; }
log_warn()  { echo -e "${YELLOW}[!]${NC} $1"; }
log_error() { echo -e "${RED}[x]${NC} $1"; }

# ─── 权限检查 ──────────────────────────────────────────
if [ "${AGENTSHELL_SKIP_ROOT_CHECK:-0}" != "1" ] && [ "$(id -u)" -ne 0 ]; then
    log_error "请使用 sudo 运行此脚本"
    exit 1
fi

# 获取实际用户的 HOME 目录（sudo 下 ${HOME} 是 /root）
REAL_USER="${SUDO_USER:-$(id -un)}"
if [ -n "$REAL_USER" ] && [ "$REAL_USER" != "root" ]; then
    REAL_HOME="$(getent passwd "$REAL_USER" | cut -d: -f6)"
    if [ -z "$REAL_HOME" ]; then
        log_error "无法解析用户 ${REAL_USER} 的主目录"
        exit 1
    fi
else
    REAL_HOME="${HOME:-/root}"
fi
CONFIG_DIR="${AGENTSHELL_CONFIG_DIR:-${REAL_HOME}/.agentshell}"
log_info "实际用户: ${REAL_USER:-root}, 配置目录: ${CONFIG_DIR}"

# ─── 平台检测 ──────────────────────────────────────────
OS="$(uname -s)"
ARCH="$(uname -m)"

case "$OS" in
    Linux)  PLATFORM="linux" ;;
    *)      log_error "不支持的操作系统: $OS"; exit 1 ;;
esac

case "$ARCH" in
    x86_64)  ARCH="x64" ;;
    aarch64) ARCH="arm64" ;;
    *)       log_error "不支持的架构: $ARCH"; exit 1 ;;
esac

# install.sh 只承担首次安装；更新必须经过后续独立的安全更新器。
if [ -e "${INSTALL_DIR}/${BINARY_NAME}" ]; then
    log_error "检测到既有安装；install.sh 不承担更新。请等待 P6 安全更新器或先按卸载文档处理。"
    exit 2
fi

TEMP_DIR=$(mktemp -d)
trap 'rm -rf -- "$TEMP_DIR"' EXIT

# ─── 获取最新版本号 ────────────────────────────────────
log_info "正在查询最新版本..."
VERSION=$(curl -fsSL "https://api.github.com/repos/${REPO}/releases/latest" | grep '"tag_name":' | sed -E 's/.*"v?([^"]+)".*/\1/')

if [ -z "$VERSION" ]; then
    log_error "无法获取最新版本号"
    exit 1
fi

log_info "最新版本: v${VERSION}"

# ─── 下载二进制 ─────────────────────────────────────────
DOWNLOAD_URL="https://github.com/${REPO}/releases/download/v${VERSION}/${BINARY_NAME}-${PLATFORM}-${ARCH}"
CHECKSUM_URL="${DOWNLOAD_URL}.sha256"

BINARY_PATH="${TEMP_DIR}/${BINARY_NAME}"

log_info "下载: ${DOWNLOAD_URL}"
curl -fsSL "$DOWNLOAD_URL" -o "$BINARY_PATH"

# ─── SHA256 校验 ────────────────────────────────────────
log_info "校验 SHA256..."
curl -fsSL "$CHECKSUM_URL" -o "${TEMP_DIR}/checksum"

EXPECTED=$(cut -d' ' -f1 "${TEMP_DIR}/checksum")
ACTUAL=$(sha256sum "$BINARY_PATH" | cut -d' ' -f1)

if [ "$EXPECTED" != "$ACTUAL" ]; then
    log_error "SHA256 校验失败！"
    log_error "  期望: $EXPECTED"
    log_error "  实际: $ACTUAL"
    exit 1
fi

log_info "SHA256 校验通过"

# ─── 安装二进制 ─────────────────────────────────────────
chmod +x "$BINARY_PATH"
mv "$BINARY_PATH" "${INSTALL_DIR}/${BINARY_NAME}"
log_info "二进制已安装: ${INSTALL_DIR}/${BINARY_NAME}"

# ─── 生成配置文件 ──────────────────────────────────────
mkdir -p "$CONFIG_DIR"

if [ ! -f "${CONFIG_DIR}/agentshell.toml" ]; then
    log_info "生成默认配置: ${CONFIG_DIR}/agentshell.toml"
    "${INSTALL_DIR}/${BINARY_NAME}" --generate-config > "${CONFIG_DIR}/agentshell.toml"
fi

# Ed25519 原始密钥由 daemon 首次启动时生成；不得以 OpenSSL PEM 覆盖该格式。
log_info "Ed25519 密钥将在 daemon 首次启动时生成"

# ─── 安装 systemd service ───────────────────────────────
mkdir -p "$SYSTEMD_DIR"
cat > "${SYSTEMD_DIR}/${SERVICE_NAME}.service" << EOF
[Unit]
Description=AgentShell Daemon
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=${REAL_USER:-root}
ExecStart=${INSTALL_DIR}/${BINARY_NAME}
Restart=always
RestartSec=5
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

# 安全加固
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=read-only
PrivateTmp=no
ReadWritePaths=${REAL_HOME}/.agentshell

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "${SERVICE_NAME}"
systemctl start "${SERVICE_NAME}"

log_info ""
log_info "════════════════════════════════════════════════"
log_info "  AgentShell 守护进程安装完成！"
log_info "════════════════════════════════════════════════"
log_info "  服务状态: systemctl status ${SERVICE_NAME}"
log_info "  配置文件: ${CONFIG_DIR}/agentshell.toml"
log_info "  日志:     journalctl -u ${SERVICE_NAME} -f"
log_info "  设备绑定请按 README 中的 Android SSH 挑战—应答流程完成。"
log_info "════════════════════════════════════════════════"
