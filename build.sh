#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

PROJECT_FILE="src/LlmodelBox.csproj"
TEST_PROJECT="tests/LlmodelBox.Tests/LlmodelBox.Tests.csproj"
SERVER_SCRIPT="tools/local-download-server.py"

MODE=""
PACKAGE=""
SERVE=""
PORT="8080"
RUN_TESTS="auto"
AUTO_INSTALL="yes"

usage() {
  cat <<'EOF'
LlmodelBox Dual Builder (Linux / Windows)

Usage:
  ./build.sh [options]

Options:
  --mode <linux|windows>         Choose build profile (interactive prompt if omitted)
  --package <standalone|light>   standalone = self-contained single-file .exe
                                 light      = framework-dependent (app/improved)
  --serve                        Start local download website after build
  --no-serve                     Skip local download website
  --port <port>                  Port for local download website (default: 8080)
  --skip-tests                   Skip dotnet test
  --no-auto-install              Check requirements only; do not auto-install missing tools
  -h, --help                     Show this help
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --mode)
      MODE="${2:-}"; shift 2 ;;
    --package)
      PACKAGE="${2:-}"; shift 2 ;;
    --serve)
      SERVE="yes"; shift ;;
    --no-serve)
      SERVE="no"; shift ;;
    --port)
      PORT="${2:-8080}"; shift 2 ;;
    --skip-tests)
      RUN_TESTS="no"; shift ;;
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

# Include user-local .dotnet install if present
if [[ -d "$HOME/.dotnet" ]]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  case ":$PATH:" in
    *":$HOME/.dotnet:"*) ;;
    *) export PATH="$HOME/.dotnet:$PATH" ;;
  esac
fi

run_privileged() {
  if [[ "$(id -u)" -eq 0 ]]; then
    "$@"
  elif command -v sudo >/dev/null 2>&1; then
    sudo "$@"
  else
    echo "Error: root or sudo is required to install system package: $*" >&2
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
    echo "Error: no supported package manager found (apt, dnf, yum, pacman, zypper, apk) to install '$pkg'." >&2
    return 1
  fi
}

has_dotnet8_or_newer() {
  if ! command -v dotnet >/dev/null 2>&1; then
    return 1
  fi
  local sdks
  sdks="$(dotnet --list-sdks 2>/dev/null || true)"
  if echo "$sdks" | grep -Eq '^(8|9|10)\.'; then
    return 0
  fi
  return 1
}

get_dotnet_sdk_label() {
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "not installed"
    return
  fi
  local ver
  ver="$(dotnet --list-sdks 2>/dev/null | awk '/^(8|9|10)\./ {print $1}' | tail -n 1)"
  if [[ -n "$ver" ]]; then
    echo "$ver"
  else
    echo "unsupported ($(dotnet --version 2>/dev/null || echo unknown))"
  fi
}

install_dotnet8_sdk() {
  echo "Downloading .NET 8.0 SDK via official Microsoft dotnet-install.sh..."
  local installer
  installer="$(mktemp /tmp/dotnet-install.XXXXXX.sh)"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
  elif command -v wget >/dev/null 2>&1; then
    wget -qO "$installer" https://dot.net/v1/dotnet-install.sh
  else
    rm -f "$installer"
    echo "Error: neither curl nor wget is available to download .NET 8 SDK." >&2
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

check_and_install_requirements() {
  echo "=========================================="
  echo "  Checking Build Requirements ($HOST_OS)"
  echo "=========================================="

  local missing_dotnet=0
  local missing_downloader=0
  local missing_python=0

  if has_dotnet8_or_newer; then
    echo "  [OK]      dotnet SDK 8.0+ ($(get_dotnet_sdk_label))"
  else
    echo "  [MISSING] dotnet SDK 8.0+ ($(get_dotnet_sdk_label))"
    missing_dotnet=1
  fi

  if command -v curl >/dev/null 2>&1; then
    echo "  [OK]      curl"
  elif command -v wget >/dev/null 2>&1; then
    echo "  [OK]      wget"
  else
    echo "  [MISSING] curl / wget"
    missing_downloader=1
  fi

  if command -v python3 >/dev/null 2>&1; then
    echo "  [OK]      python3 ($(python3 --version 2>/dev/null | awk '{print $2}'))"
  elif command -v python >/dev/null 2>&1; then
    echo "  [OK]      python ($(python --version 2>/dev/null | awk '{print $2}'))"
  else
    echo "  [MISSING] python3 (required for zip packaging & local download site)"
    missing_python=1
  fi

  if [[ "$missing_dotnet" -eq 0 && "$missing_python" -eq 0 ]]; then
    echo "All required tools are satisfied."
    echo ""
    return 0
  fi

  if [[ "$HOST_OS" != "Linux" ]]; then
    if [[ "$missing_dotnet" -eq 1 ]]; then
      echo "Error: .NET 8.0+ SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/8.0" >&2
      return 1
    fi
    return 0
  fi

  if [[ "$AUTO_INSTALL" != "yes" ]]; then
    echo "Error: missing requirements detected and --no-auto-install was set." >&2
    return 1
  fi

  echo ""
  echo "Installing missing Linux requirements..."

  if [[ "$missing_downloader" -eq 1 && "$missing_dotnet" -eq 1 ]]; then
    install_system_package curl
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

  echo ""
  echo "Re-checking requirements after installation..."
  if ! has_dotnet8_or_newer; then
    echo "Error: .NET 8.0+ SDK installation failed or is not on PATH." >&2
    return 1
  fi
  echo "  [OK]      dotnet SDK 8.0+ ($(get_dotnet_sdk_label))"

  if command -v python3 >/dev/null 2>&1 || command -v python >/dev/null 2>&1; then
    echo "  [OK]      python3"
  else
    echo "Warning: python3 is still unavailable; zip packaging and local download site will be skipped." >&2
  fi
  echo ""
}

check_and_install_requirements

DEFAULT_MODE="1"
if [[ "$HOST_OS" == MINGW* || "$HOST_OS" == MSYS* || "$HOST_OS" == CYGWIN* ]]; then
  DEFAULT_MODE="2"
fi

if [[ -z "$MODE" ]]; then
  echo "=========================================="
  echo "  LlmodelBox Dual Builder"
  echo "=========================================="
  echo "Detected Host: $HOST_OS"
  echo ""
  echo "Choose build profile:"
  echo "  [1] Linux Cross-Build -> Windows .exe + Local Download Website"
  echo "  [2] Windows Build     -> Local .exe (app/improved or standalone)"
  read -r -p "Select [1/2] (default ${DEFAULT_MODE}): " choice
  choice="${choice:-$DEFAULT_MODE}"
  case "$choice" in
    1) MODE="linux" ;;
    2) MODE="windows" ;;
    *) echo "Invalid choice: $choice" >&2; exit 1 ;;
  esac
fi

if [[ -z "$PACKAGE" ]]; then
  echo ""
  echo "Choose packaging mode:"
  echo "  [1] Portable Single-File .exe (Self-Contained win-x64, no .NET runtime needed)"
  echo "  [2] Lightweight Framework-Dependent (app/improved, requires .NET 8 Desktop Runtime)"
  read -r -p "Select [1/2] (default 1): " pkg_choice
  pkg_choice="${pkg_choice:-1}"
  case "$pkg_choice" in
    1) PACKAGE="standalone" ;;
    2) PACKAGE="light" ;;
    *) echo "Invalid choice: $pkg_choice" >&2; exit 1 ;;
  esac
fi

if [[ "$MODE" == "linux" ]]; then
  if [[ -z "$SERVE" ]]; then
    read -r -p "Start local download website after build? [Y/n]: " serve_choice
    serve_choice="${serve_choice:-Y}"
    if [[ "$serve_choice" =~ ^[Nn]$ ]]; then
      SERVE="no"
    else
      SERVE="yes"
    fi
  fi
elif [[ "$MODE" == "windows" ]]; then
  if [[ -z "$SERVE" ]]; then
    read -r -p "Start local download website after build? [y/N]: " serve_choice
    serve_choice="${serve_choice:-N}"
    if [[ "$serve_choice" =~ ^[Yy]$ ]]; then
      SERVE="yes"
    else
      SERVE="no"
    fi
  fi
else
  echo "Invalid --mode '$MODE'. Expected 'linux' or 'windows'." >&2
  exit 1
fi

run_build_pipeline() {
  # Note: WPF xUnit test execution requires Windows Desktop runtime; on Linux we compile tests.
  if [[ "$RUN_TESTS" != "no" ]]; then
    if [[ "$HOST_OS" == "Linux" || "$HOST_OS" == "Darwin" ]]; then
      echo ""
      echo "[1/3] Compiling test suite (Windows test host execution skipped on $HOST_OS)..."
      dotnet build "$TEST_PROJECT" -c Release -p:EnableWindowsTargeting=true
    else
      echo ""
      echo "[1/3] Running automated tests..."
      dotnet test "$TEST_PROJECT"
    fi
  fi

  echo ""
  echo "[2/3] Publishing LlmodelBox (win-x64, package=$PACKAGE)..."

  if [[ "$PACKAGE" == "standalone" ]]; then
    OUT_DIR="dist/win-x64-standalone"
    PKG_LABEL="Portable Single-File (Self-Contained win-x64)"
    dotnet publish "$PROJECT_FILE" \
      -c Release \
      -r win-x64 \
      --self-contained true \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:EnableWindowsTargeting=true \
      -o "$OUT_DIR"
  elif [[ "$PACKAGE" == "light" ]]; then
    OUT_DIR="app/improved"
    PKG_LABEL="Framework-Dependent (win-x64)"
    dotnet publish "$PROJECT_FILE" \
      -c Release \
      -r win-x64 \
      --no-self-contained \
      -p:EnableWindowsTargeting=true \
      -o "$OUT_DIR"
  else
    echo "Invalid --package '$PACKAGE'. Expected 'standalone' or 'light'." >&2
    return 1
  fi
}

# Run build with automatic requirement re-check, NuGet restore, and retry on failure
if ! run_build_pipeline; then
  echo ""
  echo "Initial build attempt failed. Re-checking requirements and restoring packages before retry..." >&2
  check_and_install_requirements
  dotnet restore "$PROJECT_FILE" -r win-x64 -p:EnableWindowsTargeting=true
  dotnet restore "$TEST_PROJECT" -r win-x64 -p:EnableWindowsTargeting=true
  echo ""
  echo "Retrying build..."
  run_build_pipeline
fi

RELEASE_DIR="dist/release"
echo ""
echo "[3/3] Staging release artifacts..."

if command -v python3 >/dev/null 2>&1; then
  PY_BIN="python3"
elif command -v python >/dev/null 2>&1; then
  PY_BIN="python"
else
  PY_BIN=""
fi

if [[ -n "$PY_BIN" ]]; then
  if [[ "$SERVE" == "yes" ]]; then
    exec "$PY_BIN" "$SERVER_SCRIPT" \
      --build-dir "$OUT_DIR" \
      --release-dir "$RELEASE_DIR" \
      --repo-root "$SCRIPT_DIR" \
      --package-label "$PKG_LABEL" \
      --port "$PORT"
  else
    "$PY_BIN" "$SERVER_SCRIPT" \
      --build-dir "$OUT_DIR" \
      --release-dir "$RELEASE_DIR" \
      --repo-root "$SCRIPT_DIR" \
      --package-label "$PKG_LABEL" \
      --port "$PORT" \
      --prepare-only
  fi
else
  echo "Build succeeded: $OUT_DIR/LlmodelBox.exe"
  if [[ "$SERVE" == "yes" ]]; then
    echo "Error: python3 is required to run the local download website." >&2
    exit 1
  fi
fi
