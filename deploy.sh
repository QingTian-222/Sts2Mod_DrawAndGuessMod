#!/usr/bin/env bash
#
# deploy.sh -- Build and deploy DrawAndGuessMod to the local macOS game and a
#              LAN Windows machine over SMB.
#
# Usage:
#   ./deploy.sh                          Build Release and deploy to both targets
#   ./deploy.sh --deploy-only [out_dir]  Skip build, deploy only (used by csproj)
#   ./deploy.sh --logs                   Tail the newest game log from the Windows share
#
# LAN paths are configured in .env at the project root, e.g.:
#   WIN_MOD_DIR="smb://192.168.1.2/sts2app/mods/DrawAndGuessMod"
#   WIN_LOG_DIR="smb://192.168.1.2/sts2app/logs"
#
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$PROJECT_DIR"

GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
MAC_MOD_DIR="${MAC_MOD_DIR:-$GAME_DIR/SlayTheSpire2.app/Contents/MacOS/mods/DrawAndGuessMod}"
STS2_DATA_DIR="${STS2_DATA_DIR:-$GAME_DIR/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64}"
ENV_FILE="$PROJECT_DIR/.env"
CONFIGURATION="${CONFIGURATION:-Release}"

log()  { printf '\033[36m[deploy]\033[0m %s\n' "$*"; }
warn() { printf '\033[33m[deploy] WARN: %s\033[0m\n' "$*" >&2; }
die()  { printf '\033[31m[deploy] ERROR: %s\033[0m\n' "$*" >&2; exit 1; }

# --- locate dotnet ---
DOTNET_BIN="${DOTNET:-}"
if [ -z "$DOTNET_BIN" ]; then
  if command -v dotnet >/dev/null 2>&1; then
    DOTNET_BIN="dotnet"
  elif [ -x /usr/local/share/dotnet/dotnet ]; then
    DOTNET_BIN="/usr/local/share/dotnet/dotnet"
  elif [ -n "${DOTNET_ROOT:-}" ] && [ -x "$DOTNET_ROOT/dotnet" ]; then
    DOTNET_BIN="$DOTNET_ROOT/dotnet"
  else
    die "dotnet SDK not found (checked PATH and /usr/local/share/dotnet)"
  fi
fi

# --- read .env ---
if [ -f "$ENV_FILE" ]; then
  set -a
  # shellcheck disable=SC1090
  . "$ENV_FILE"
  set +a
fi
WIN_MOD_DIR="${WIN_MOD_DIR:-}"
WIN_LOG_DIR="${WIN_LOG_DIR:-}"

# --- args ---
MODE="build-deploy"
OUT_DIR=""
if [ "${1:-}" = "--deploy-only" ]; then
  MODE="deploy-only"
  OUT_DIR="${2:-}"
elif [ "${1:-}" = "--logs" ]; then
  MODE="logs"
fi
[ -n "$OUT_DIR" ] || OUT_DIR="$PROJECT_DIR/.godot/mono/temp/bin/$CONFIGURATION"

# --- helpers ---
latest_pkg() {
  ls -d "$HOME/.nuget/packages/$1/"*/ 2>/dev/null | sort -V | tail -1
}

# parse smb://host/share/sub/path into SMB_HOST / SMB_SHARE / SMB_SUBPATH
parse_smb_url() {
  local url="$1" rest
  case "$url" in
    smb://*) ;;
    *) return 1 ;;
  esac
  rest="${url#smb://}"
  rest="${rest%/}"
  SMB_HOST="${rest%%/*}"
  rest="${rest#*/}"
  SMB_SHARE="${rest%%/*}"
  if [ "$rest" = "$SMB_SHARE" ]; then
    SMB_SUBPATH=""
  else
    SMB_SUBPATH="${rest#*/}"
  fi
  [ -n "$SMB_HOST" ] && [ -n "$SMB_SHARE" ]
}

# mount an smb share if needed; prints the mount point on success
ensure_smb_mount() {
  local host="$1" share="$2" mp="/Volumes/$2"
  if mount | grep -q "on $mp (smbfs"; then
    echo "$mp"
    return 0
  fi
  [ -d "$mp" ] || mkdir -p "$mp" 2>/dev/null || true
  # try keychain-saved credentials first, then guest
  if mount_smbfs "//$host/$share" "$mp" >/dev/null 2>&1; then
    echo "$mp"
    return 0
  fi
  if mount_smbfs -N "//GUEST@$host/$share" "$mp" >/dev/null 2>&1; then
    echo "$mp"
    return 0
  fi
  return 1
}

# copy staged files over an smb share, replacing in place when files are locked
copy_tree_smb() {
  local src="$1" dest="$2"
  (cd "$src" && find . -type f) | while IFS= read -r rel; do
    mkdir -p "$dest/$(dirname "$rel")"
    if ! cp -f "$src/$rel" "$dest/$rel" 2>/dev/null; then
      # Windows 文件锁只锁句柄/重命名，不锁内容写入：用 cat 直接覆盖
      if cat "$src/$rel" > "$dest/$rel" 2>/dev/null; then
        echo "    [locked] $rel -> content overwritten in place" >&2
      else
        cp "$src/$rel" "$dest/$rel.new" 2>/dev/null && echo "    [locked] $rel -> written as $rel.new (restart game to apply)" >&2
      fi
    fi
  done
}

# --- build ---
if [ "$MODE" = "build-deploy" ]; then
  log "Building $CONFIGURATION (AnyCPU, runs on both Windows x64 and macOS arm64)..."
  # SkipModDeploy prevents the csproj post-build target from recursing into this script
  "$DOTNET_BIN" build "$PROJECT_DIR/DrawAndGuessMod.csproj" \
    -c "$CONFIGURATION" --no-restore \
    -p:Sts2DataDir="$STS2_DATA_DIR" \
    -p:SkipModDeploy=true
  log "Build finished -> $OUT_DIR"
fi

# --- stage + deploy ---
if [ "$MODE" != "logs" ]; then
  [ -f "$OUT_DIR/DrawAndGuessMod.dll" ] || die "DrawAndGuessMod.dll not found in $OUT_DIR (build first)"

  ORT_DIR="$(latest_pkg microsoft.ml.onnxruntime)"
  ORT_MANAGED_DIR="$(latest_pkg microsoft.ml.onnxruntime.managed)"
  TENSORS_DIR="$(latest_pkg system.numerics.tensors)"
  [ -n "$ORT_DIR" ] && [ -n "$ORT_MANAGED_DIR" ] && [ -n "$TENSORS_DIR" ] || die "onnxruntime/tensors missing from NuGet cache; run dotnet restore"

  STAGE="$(mktemp -d)/DrawAndGuessMod"
  mkdir -p "$STAGE/Models"
  cp "$OUT_DIR/DrawAndGuessMod.dll" "$STAGE/"
  cp "$OUT_DIR/DrawAndGuessMod.pdb" "$STAGE/" 2>/dev/null || true
  cp "$PROJECT_DIR/DrawAndGuessMod.json" "$STAGE/"
  cp "$PROJECT_DIR/Models/card_features.bin" "$PROJECT_DIR/Models/card_dino_features.bin" "$PROJECT_DIR/Models/dinov2_vits14.onnx" "$STAGE/Models/"
  cp "${ORT_MANAGED_DIR}lib/net8.0/Microsoft.ML.OnnxRuntime.dll" "$STAGE/"
  cp "${TENSORS_DIR}lib/net9.0/System.Numerics.Tensors.dll" "$STAGE/"
  # Windows x64 native runtime
  cp "${ORT_DIR}runtimes/win-x64/native/onnxruntime.dll" "$STAGE/"
  cp "${ORT_DIR}runtimes/win-x64/native/onnxruntime_providers_shared.dll" "$STAGE/"
  # macOS arm64 native runtime
  cp "${ORT_DIR}runtimes/osx-arm64/native/libonnxruntime.dylib" "$STAGE/"

  # deploy: local macOS game
  log "Deploying to local macOS game -> $MAC_MOD_DIR"
  mkdir -p "$MAC_MOD_DIR"
  rm -rf "${MAC_MOD_DIR:?}"/*
  cp -Rf "$STAGE/." "$MAC_MOD_DIR/"
  log "OK: macOS deploy done"

  # deploy: LAN Windows machine over smb
  if [ -n "$WIN_MOD_DIR" ]; then
    if parse_smb_url "$WIN_MOD_DIR"; then
      if MP="$(ensure_smb_mount "$SMB_HOST" "$SMB_SHARE")"; then
        WIN_DEST="$MP/$SMB_SUBPATH"
        log "Deploying to Windows share ($WIN_MOD_DIR)..."
        mkdir -p "$WIN_DEST"
        copy_tree_smb "$STAGE" "$WIN_DEST"
        log "OK: Windows deploy done -> smb://$SMB_HOST/$SMB_SHARE/$SMB_SUBPATH"
      else
        warn "Cannot mount smb://$SMB_HOST/$SMB_SHARE. Open it once in Finder to save credentials:"
        warn "    open \"smb://$SMB_HOST/$SMB_SHARE\""
        warn "Then re-run this script. Skipping Windows deploy this time."
      fi
    else
      warn "WIN_MOD_DIR is not an smb:// URL: $WIN_MOD_DIR (skipped)"
    fi
  else
    warn "WIN_MOD_DIR not set in .env, skipping Windows deploy"
  fi

  rm -rf "$(dirname "$STAGE")"
fi

# --- tail Windows game log ---
if [ "$MODE" = "logs" ]; then
  [ -n "$WIN_LOG_DIR" ] || die "WIN_LOG_DIR not set in .env"
  parse_smb_url "$WIN_LOG_DIR" || die "WIN_LOG_DIR is not an smb:// URL: $WIN_LOG_DIR"
  MP="$(ensure_smb_mount "$SMB_HOST" "$SMB_SHARE")" || die "Cannot mount smb://$SMB_HOST/$SMB_SHARE"
  LOG_DIR="$MP/$SMB_SUBPATH"
  LATEST="$(ls -t "$LOG_DIR"/godot*.log 2>/dev/null | head -1)"
  [ -n "$LATEST" ] || die "no godot*.log under $LOG_DIR"
  log "Newest log: $LATEST"
  tail -n 60 "$LATEST"
fi
