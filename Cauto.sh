#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

REPO_URL=""
BRANCH=""
PACKAGE=""
PORT="8080"
INTERVAL="30"
WORK_DIR="$HOME/.llmodelbox-cauto"
ONCE="no"
NON_INTERACTIVE="no"
AUTO_INSTALL="yes"
SKIP_TESTS="no"
SERVER_PID=""

usage() {
  cat <<'EOF'
LlmodelBox Continuous Auto-Builder & Local Distribution Host (Cauto)

Monitors a GitHub repository branch for new commits. When a new commit arrives,
it clears the workspace, performs a fresh shallow clone, builds the Windows .exe
via build.sh, and updates the live local HTTP download portal without downtime.

Usage:
  ./Cauto.sh [options]

Options:
  --repo <url>                   GitHub repository URL (auto-detected from origin if omitted)
  --branch <name>                Branch to monitor (default: main or current branch)
  --package <standalone|light>   standalone = self-contained single-file .exe (default)
                                 light      = framework-dependent (app/improved)
  --port <port>                  Local HTTP download website port (default: 8080)
  --interval <seconds>           Poll interval for checking new commits (default: 30)
  --workdir <dir>                Clean build/staging workspace outside repo (default: ~/.llmodelbox-cauto)
  --skip-tests                   Pass --skip-tests to build.sh
  --once                         Run one clean clone + build + serve, then wait on server
  --yes                          Non-interactive mode (use detected/default options)
  --no-auto-install              Check requirements only; do not auto-install missing tools
  -h, --help                     Show this help
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo)
      REPO_URL="${2:-}"; shift 2 ;;
    --branch)
      BRANCH="${2:-}"; shift 2 ;;
    --package)
      PACKAGE="${2:-}"; shift 2 ;;
    --port)
      PORT="${2:-8080}"; shift 2 ;;
    --interval)
      INTERVAL="${2:-30}"; shift 2 ;;
    --workdir)
      WORK_DIR="${2:-$HOME/.llmodelbox-cauto}"; shift 2 ;;
    --skip-tests)
      SKIP_TESTS="yes"; shift ;;
    --once)
      ONCE="yes"; shift ;;
    --yes)
      NON_INTERACTIVE="yes"; shift ;;
    --no-auto-install)
      AUTO_INSTALL="no"; shift ;;
    -h|--help)
      usage; exit 0 ;;
    *)
      echo "Unknown option: $1" >&2
      usage
      exit 1 ;;
  esac
done

HOST_OS="$(uname -s)"
if [[ -d "$HOME/.dotnet" ]]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  case ":$PATH:" in
    *":$HOME/.dotnet:"*) ;;
    *) export PATH="$HOME/.dotnet:$PATH" ;;
  esac
fi

cleanup() {
  if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
    echo ""
    echo "[cauto] Stopping local download server (PID $SERVER_PID)..."
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

run_privileged() {
  if [[ "$(id -u)" -eq 0 ]]; then
    "$@"
  elif command -v sudo >/dev/null 2>&1; then
    sudo "$@"
  else
    echo "Error: root or sudo is required to install: $*" >&2
    return 1
  fi
}

install_system_package() {
  local pkg="$1"
  if command -v apt-get >/dev/null 2>&1; then
    run_privileged apt-get update -qq
    run_privileged apt-get install -y "$pkg"
  elif command -v dnf >/dev/null 2>&1; then
    run_privileged dnf install -y "$pkg"
  elif command -v yum >/dev/null 2>&1; then
    run_privileged yum install -y "$pkg"
  elif command -v pacman >/dev/null 2>&1; then
    run_privileged pacman -Sy --noconfirm "$pkg"
  elif command -v zypper >/dev/null 2>&1; then
    run_privileged zypper --non-interactive install "$pkg"
  elif command -v apk >/dev/null 2>&1; then
    run_privileged apk add --no-cache "$pkg"
  else
    echo "Error: no supported package manager found to install '$pkg'." >&2
    return 1
  fi
}

has_dotnet8_or_newer() {
  command -v dotnet >/dev/null 2>&1 || return 1
  dotnet --list-sdks 2>/dev/null | grep -Eq '^(8|9|10)\.'
}

install_dotnet8_sdk() {
  echo "[cauto] Installing .NET 8.0 SDK into $HOME/.dotnet..."
  local installer
  installer="$(mktemp /tmp/dotnet-install.XXXXXX.sh)"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
  elif command -v wget >/dev/null 2>&1; then
    wget -qO "$installer" https://dot.net/v1/dotnet-install.sh
  else
    rm -f "$installer"
    echo "Error: curl or wget is required to download .NET 8 SDK." >&2
    return 1
  fi
  chmod +x "$installer"
  bash "$installer" --channel 8.0 --install-dir "$HOME/.dotnet"
  rm -f "$installer"
  export DOTNET_ROOT="$HOME/.dotnet"
  case ":$PATH:" in
    *":$HOME/.dotnet:"*) ;;
    *) export PATH="$HOME/.dotnet:$PATH" ;;
  esac
}

ensure_requirements() {
  echo "=========================================="
  echo "  Cauto Requirement Check ($HOST_OS)"
  echo "=========================================="
  local missing_git=0 missing_dotnet=0 missing_python=0 missing_dl=0

  if command -v git >/dev/null 2>&1; then
    echo "  [OK]      git ($(git --version | awk '{print $3}'))"
  else
    echo "  [MISSING] git"
    missing_git=1
  fi

  if has_dotnet8_or_newer; then
    echo "  [OK]      dotnet SDK 8.0+"
  else
    echo "  [MISSING] dotnet SDK 8.0+"
    missing_dotnet=1
  fi

  if command -v python3 >/dev/null 2>&1 || command -v python >/dev/null 2>&1; then
    echo "  [OK]      python3"
  else
    echo "  [MISSING] python3"
    missing_python=1
  fi

  if command -v curl >/dev/null 2>&1 || command -v wget >/dev/null 2>&1; then
    echo "  [OK]      curl / wget"
  else
    echo "  [MISSING] curl / wget"
    missing_dl=1
  fi

  if [[ "$missing_git" -eq 0 && "$missing_dotnet" -eq 0 && "$missing_python" -eq 0 ]]; then
    echo "All Cauto requirements are satisfied."
    echo ""
    return 0
  fi

  if [[ "$HOST_OS" != "Linux" ]]; then
    echo "Error: missing required tools on $HOST_OS (git, .NET 8 SDK, python3)." >&2
    return 1
  fi

  if [[ "$AUTO_INSTALL" != "yes" ]]; then
    echo "Error: missing requirements and --no-auto-install was set." >&2
    return 1
  fi

  echo ""
  echo "[cauto] Installing missing requirements..."
  if [[ "$missing_dl" -eq 1 && "$missing_dotnet" -eq 1 ]]; then
    install_system_package curl
  fi
  if [[ "$missing_git" -eq 1 ]]; then
    install_system_package git
  fi
  if [[ "$missing_python" -eq 1 ]]; then
    if command -v pacman >/dev/null 2>&1; then
      install_system_package python
    else
      install_system_package python3
    fi
  fi
  if [[ "$missing_dotnet" -eq 1 ]]; then
    install_dotnet8_sdk
  fi

  if ! command -v git >/dev/null 2>&1 || ! has_dotnet8_or_newer; then
    echo "Error: requirement installation failed." >&2
    return 1
  fi
  echo ""
}

ensure_requirements

if command -v python3 >/dev/null 2>&1; then
  PY_BIN="python3"
elif command -v python >/dev/null 2>&1; then
  PY_BIN="python"
else
  echo "Error: python3 is required to run the local download server." >&2
  exit 1
fi

# Auto-detect repo URL & branch from current directory if available, else default to R2Ksanu/LLMBOX-FT
DEFAULT_REPO_URL="https://github.com/R2Ksanu/LLMBOX-FT.git"
DETECTED_URL="$DEFAULT_REPO_URL"
DETECTED_BRANCH="main"
if command -v git >/dev/null 2>&1 && git -C "$SCRIPT_DIR" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  origin_url="$(git -C "$SCRIPT_DIR" config --get remote.origin.url || true)"
  if [[ -n "$origin_url" ]]; then
    DETECTED_URL="$origin_url"
  fi
  cur_branch="$(git -C "$SCRIPT_DIR" rev-parse --abbrev-ref HEAD 2>/dev/null || true)"
  if [[ -n "$cur_branch" && "$cur_branch" != "HEAD" ]]; then
    DETECTED_BRANCH="$cur_branch"
  fi
fi

if [[ -z "$REPO_URL" ]]; then
  if [[ "$NON_INTERACTIVE" == "yes" ]]; then
    REPO_URL="$DETECTED_URL"
  else
    read -r -p "GitHub repo URL (default: $DETECTED_URL): " input_url
    REPO_URL="${input_url:-$DETECTED_URL}"
  fi
fi

# Normalize pasted GitHub web URLs like https://github.com/user/repo/tree/main -> .git
if [[ "$REPO_URL" =~ ^(https://github\.com/[^/]+/[^/]+)/tree/([^/]+)/?$ ]]; then
  REPO_URL="${BASH_REMATCH[1]}.git"
  if [[ -z "$BRANCH" ]]; then
    BRANCH="${BASH_REMATCH[2]}"
  fi
fi

if [[ -z "$REPO_URL" ]]; then
  echo "Error: a GitHub repository URL is required (--repo <url>)." >&2
  exit 1
fi

if [[ -z "$BRANCH" ]]; then
  if [[ "$NON_INTERACTIVE" == "yes" ]]; then
    BRANCH="$DETECTED_BRANCH"
  else
    read -r -p "Branch to watch (default: $DETECTED_BRANCH): " input_branch
    BRANCH="${input_branch:-$DETECTED_BRANCH}"
  fi
fi

if [[ -z "$PACKAGE" ]]; then
  if [[ "$NON_INTERACTIVE" == "yes" ]]; then
    PACKAGE="standalone"
  else
    echo ""
    echo "Choose packaging mode:"
    echo "  [1] Portable Single-File .exe (Self-Contained win-x64)"
    echo "  [2] Lightweight Framework-Dependent (app/improved)"
    read -r -p "Select [1/2] (default 1): " pkg_choice
    pkg_choice="${pkg_choice:-1}"
    case "$pkg_choice" in
      1) PACKAGE="standalone" ;;
      2) PACKAGE="light" ;;
      *) echo "Invalid choice: $pkg_choice" >&2; exit 1 ;;
    esac
  fi
fi

mkdir -p "$WORK_DIR"
REPO_DIR="$WORK_DIR/repo"
RELEASE_DIR="$WORK_DIR/release"
STATE_FILE="$WORK_DIR/last_commit.txt"
ATTEMPT_FILE="$WORK_DIR/last_attempted.txt"
SERVER_COPY="$WORK_DIR/local-download-server.py"

if [[ -f "$SCRIPT_DIR/tools/local-download-server.py" ]]; then
  cp "$SCRIPT_DIR/tools/local-download-server.py" "$SERVER_COPY"
fi

start_server_if_needed() {
  if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
    return 0
  fi
  if [[ -n "${CODESPACE_NAME:-}" && "$(id -u)" -eq 0 ]]; then
    echo "[cauto] Warning: running as root inside GitHub Codespaces (CODESPACE_NAME=$CODESPACE_NAME)."
    echo "[cauto] Run Cauto.sh as your normal codespace user (without 'sudo su') so VS Code port forwarding stays attached cleanly."
  fi

  if [[ ! -f "$RELEASE_DIR/LlmodelBox.exe" || ! -f "$SERVER_COPY" ]]; then
    return 0
  fi
  echo "[cauto] Starting persistent local download server on port $PORT..."
  "$PY_BIN" "$SERVER_COPY" \
    --release-dir "$RELEASE_DIR" \
    --port "$PORT" \
    --serve-only &
  SERVER_PID=$!

  sleep 1
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    echo "[cauto] Error: local download server failed to start on port $PORT (port may already be in use)." >&2
    SERVER_PID=""
    return 1
  fi

  if command -v curl >/dev/null 2>&1; then
    if curl -fsS --max-time 3 "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then
      echo "[cauto] Verified local portal health: http://127.0.0.1:$PORT/health"
    else
      echo "[cauto] Warning: server PID $SERVER_PID is running, but http://127.0.0.1:$PORT/health did not respond yet." >&2
    fi
  fi

  if [[ -n "${CODESPACE_NAME:-}" ]] && command -v gh >/dev/null 2>&1; then
    gh codespace ports visibility "${PORT}:public" -c "$CODESPACE_NAME" >/dev/null 2>&1 || true
  fi
}

get_remote_sha() {
  git ls-remote "$REPO_URL" "refs/heads/$BRANCH" 2>/dev/null | awk 'NR==1 {print $1}'
}

log_cauto() {
  echo "[$(date -u '+%Y-%m-%d %H:%M:%S UTC')] [cauto] $*"
}

run_clean_build() {
  local target_sha="$1"
  local short_sha="${target_sha:0:7}"

  echo ""
  echo "=========================================="
  log_cauto "Clean Re-Clone & Build @ $short_sha"
  echo "=========================================="

  # Clear previous clone completely
  rm -rf "$REPO_DIR"
  log_cauto "Cloning $REPO_URL (branch: $BRANCH)..."
  git clone --depth 1 --branch "$BRANCH" "$REPO_URL" "$REPO_DIR"

  local cloned_sha
  cloned_sha="$(git -C "$REPO_DIR" rev-parse HEAD)"
  short_sha="${cloned_sha:0:7}"

  chmod +x "$REPO_DIR/build.sh"
  local build_args=(--mode linux --package "$PACKAGE" --no-serve)
  if [[ "$SKIP_TESTS" == "yes" ]]; then
    build_args+=(--skip-tests)
  fi

  log_cauto "Running build.sh (${build_args[*]})..."
  (cd "$REPO_DIR" && ./build.sh "${build_args[@]}")

  local out_dir pkg_label
  if [[ "$PACKAGE" == "standalone" ]]; then
    out_dir="$REPO_DIR/dist/win-x64-standalone"
    pkg_label="Portable Single-File (Self-Contained win-x64)"
  else
    out_dir="$REPO_DIR/app/improved"
    pkg_label="Framework-Dependent (win-x64)"
  fi

  cp "$REPO_DIR/tools/local-download-server.py" "$SERVER_COPY"
  "$PY_BIN" "$SERVER_COPY" \
    --build-dir "$out_dir" \
    --release-dir "$RELEASE_DIR" \
    --repo-root "$REPO_DIR" \
    --package-label "$pkg_label" \
    --branch "$BRANCH" \
    --commit "$short_sha" \
    --prepare-only

  printf '%s\n' "$cloned_sha" > "$STATE_FILE"
  log_cauto "Release updated cleanly for commit $short_sha."
  start_server_if_needed
}

echo ""
echo "=========================================="
echo "  LlmodelBox Cauto Watcher Active"
echo "=========================================="
echo "  Started:  $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
echo "  Repo:     $REPO_URL"
echo "  Branch:   $BRANCH"
echo "  Package:  $PACKAGE"
echo "  Port:     $PORT"
echo "  Interval: ${INTERVAL}s"
echo "  Workdir:  $WORK_DIR"
echo "=========================================="

# If a prior staged release exists, start serving immediately while checking remote
start_server_if_needed

while true; do
  remote_sha="$(get_remote_sha || true)"
  if [[ -z "$remote_sha" ]]; then
    log_cauto "Warning: could not query $REPO_URL ($BRANCH). Retrying in ${INTERVAL}s..."
  else
    last_built=""
    last_attempted=""
    [[ -f "$STATE_FILE" ]] && last_built="$(cat "$STATE_FILE" 2>/dev/null || true)"
    [[ -f "$ATTEMPT_FILE" ]] && last_attempted="$(cat "$ATTEMPT_FILE" 2>/dev/null || true)"

    needs_build="no"
    if [[ ! -f "$RELEASE_DIR/LlmodelBox.exe" ]]; then
      needs_build="yes"
    elif [[ "$remote_sha" != "$last_built" && "$remote_sha" != "$last_attempted" ]]; then
      needs_build="yes"
    fi

    if [[ "$needs_build" == "yes" ]]; then
      printf '%s\n' "$remote_sha" > "$ATTEMPT_FILE"
      if ! run_clean_build "$remote_sha"; then
        log_cauto "Build FAILED for ${remote_sha:0:7}. Existing release (if any) remains online. Waiting for next commit..." >&2
      fi
    else
      log_cauto "Up to date (${remote_sha:0:7})."
    fi
  fi

  if [[ "$ONCE" == "yes" ]]; then
    if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
      echo "[cauto] --once complete. Hosting local download site (Ctrl+C to stop)..."
      wait "$SERVER_PID"
    fi
    break
  fi

  sleep "$INTERVAL"
done
