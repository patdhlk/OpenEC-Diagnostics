#!/bin/sh
set -eu

# OpenEC-Diagnostics CLI installer
# Usage: curl -fsSL https://raw.githubusercontent.com/patdhlk/OpenEC-Diagnostics/main/install.sh | sh
# Or: ./install.sh [--version vX.Y.Z]

REPO="patdhlk/OpenEC-Diagnostics"
BINARY_NAME="openec"

# Cleanup temp directory on exit
cleanup() {
    if [ -n "${TMPDIR:-}" ] && [ -d "$TMPDIR" ]; then
        rm -rf "$TMPDIR"
    fi
}
trap cleanup EXIT INT TERM

# Print messages to stderr to keep stdout clean for piped execution
log() {
    printf '%s\n' "$*" >&2
}

error() {
    log "ERROR: $*"
    exit 1
}

# Detect OS
detect_os() {
    case "$(uname -s)" in
        Linux)
            echo "linux"
            ;;
        Darwin)
            echo "osx"
            ;;
        *)
            error "Unsupported operating system: $(uname -s)"
            ;;
    esac
}

# Detect architecture
detect_arch() {
    case "$(uname -m)" in
        x86_64|amd64)
            echo "x64"
            ;;
        arm64|aarch64)
            echo "arm64"
            ;;
        *)
            error "Unsupported architecture: $(uname -m)"
            ;;
    esac
}

# Fetch latest release tag from GitHub API
fetch_latest_version() {
    if ! command -v curl >/dev/null 2>&1; then
        error "curl is required but not installed. Please install curl and try again."
    fi
    
    log "Fetching latest release version..."
    RELEASE_JSON=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest") || \
        error "Failed to fetch release information from GitHub API"
    
    # Extract tag_name using grep and sed (no jq dependency)
    TAG=$(printf '%s\n' "$RELEASE_JSON" | grep '"tag_name"' | sed -E 's/.*"tag_name"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/')
    
    if [ -z "$TAG" ]; then
        error "Failed to parse tag_name from GitHub API response"
    fi
    
    echo "$TAG"
}

# Parse version argument
parse_version() {
    VERSION_ARG=""
    
    # Parse --version flag
    while [ $# -gt 0 ]; do
        case "$1" in
            --version)
                shift
                VERSION_ARG="$1"
                shift
                ;;
            *)
                shift
                ;;
        esac
    done
    
    if [ -n "$VERSION_ARG" ]; then
        # Ensure tag has leading 'v'
        case "$VERSION_ARG" in
            v*)
                TAG="$VERSION_ARG"
                NUMERIC_VERSION="${VERSION_ARG#v}"
                ;;
            *)
                TAG="v$VERSION_ARG"
                NUMERIC_VERSION="$VERSION_ARG"
                ;;
        esac
        log "Using specified version: $TAG"
    else
        TAG=$(fetch_latest_version)
        # Strip leading 'v' for numeric version
        NUMERIC_VERSION="${TAG#v}"
        log "Using latest version: $TAG"
    fi
}

# Determine install directory
determine_install_dir() {
    if [ "$(id -u)" -eq 0 ]; then
        # Running as root
        echo "/usr/local/bin"
    elif [ -d "$HOME/.local/bin" ] && [ -w "$HOME/.local/bin" ]; then
        echo "$HOME/.local/bin"
    elif [ -w "/usr/local/bin" ] 2>/dev/null; then
        echo "/usr/local/bin"
    else
        # Create ~/.local/bin if it doesn't exist
        mkdir -p "$HOME/.local/bin" 2>/dev/null || error "Cannot create install directory"
        echo "$HOME/.local/bin"
    fi
}

# Verify SHA256 checksum
verify_checksum() {
    ASSET_FILE="$1"
    CHECKSUM_FILE="$2"
    
    log "Verifying checksum..."
    
    # Extract expected hash for our asset
    EXPECTED_HASH=$(grep "$ASSET_NAME" "$CHECKSUM_FILE" | awk '{print $1}')
    
    if [ -z "$EXPECTED_HASH" ]; then
        error "Could not find checksum for $ASSET_NAME in SHA256SUMS"
    fi
    
    # Compute actual hash (use shasum on macOS, sha256sum on Linux)
    if command -v sha256sum >/dev/null 2>&1; then
        ACTUAL_HASH=$(sha256sum "$ASSET_FILE" | awk '{print $1}')
    elif command -v shasum >/dev/null 2>&1; then
        ACTUAL_HASH=$(shasum -a 256 "$ASSET_FILE" | awk '{print $1}')
    else
        error "Neither sha256sum nor shasum found. Cannot verify checksum."
    fi
    
    if [ "$EXPECTED_HASH" != "$ACTUAL_HASH" ]; then
        error "Checksum verification failed!
Expected: $EXPECTED_HASH
Got:      $ACTUAL_HASH"
    fi
    
    log "Checksum verified successfully"
}

# Check if directory is in PATH
is_in_path() {
    case ":$PATH:" in
        *:"$1":*)
            return 0
            ;;
        *)
            return 1
            ;;
    esac
}

# Main installation flow
main() {
    log "OpenEC-Diagnostics CLI Installer"
    log "================================"
    
    # Parse arguments
    parse_version "$@"
    
    # Detect system
    OS=$(detect_os)
    ARCH=$(detect_arch)
    log "Detected system: $OS-$ARCH"
    
    # Construct asset name and download URL
    ASSET_NAME="${BINARY_NAME}-${NUMERIC_VERSION}-${OS}-${ARCH}.tar.gz"
    DOWNLOAD_URL="https://github.com/$REPO/releases/download/$TAG/$ASSET_NAME"
    CHECKSUM_URL="https://github.com/$REPO/releases/download/$TAG/SHA256SUMS"
    
    log "Downloading $ASSET_NAME..."
    
    # Create temporary directory
    TMPDIR=$(mktemp -d)
    
    # Download asset and checksums
    curl -fsSL "$DOWNLOAD_URL" -o "$TMPDIR/$ASSET_NAME" || \
        error "Failed to download $ASSET_NAME from $DOWNLOAD_URL"
    
    curl -fsSL "$CHECKSUM_URL" -o "$TMPDIR/SHA256SUMS" || \
        error "Failed to download SHA256SUMS from $CHECKSUM_URL"
    
    # Verify checksum
    verify_checksum "$TMPDIR/$ASSET_NAME" "$TMPDIR/SHA256SUMS"
    
    # Extract archive
    log "Extracting archive..."
    tar -xzf "$TMPDIR/$ASSET_NAME" -C "$TMPDIR" || \
        error "Failed to extract archive"
    
    # Determine install directory
    INSTALL_DIR=$(determine_install_dir)
    log "Installing to $INSTALL_DIR..."
    
    # Ensure install directory exists
    mkdir -p "$INSTALL_DIR" 2>/dev/null || \
        error "Failed to create install directory: $INSTALL_DIR"
    
    # Install binary
    if command -v install >/dev/null 2>&1; then
        install -m 0755 "$TMPDIR/$BINARY_NAME" "$INSTALL_DIR/$BINARY_NAME" || \
            error "Failed to install binary"
    else
        cp "$TMPDIR/$BINARY_NAME" "$INSTALL_DIR/$BINARY_NAME" && \
        chmod 0755 "$INSTALL_DIR/$BINARY_NAME" || \
            error "Failed to install binary"
    fi
    
    log ""
    log "✓ Installation successful!"
    log "  Binary installed to: $INSTALL_DIR/$BINARY_NAME"
    
    # Check if install directory is in PATH
    if ! is_in_path "$INSTALL_DIR"; then
        log ""
        log "NOTE: $INSTALL_DIR is not in your PATH."
        log "Add it by appending this line to your shell profile (~/.bashrc, ~/.zshrc, etc.):"
        log "  export PATH=\"\$PATH:$INSTALL_DIR\""
    fi
    
    # Linux-specific runtime requirements
    if [ "$OS" = "linux" ]; then
        log ""
        log "Linux Runtime Requirements:"
        log "  • libpcap0.8 and libssl3 must be installed for live packet capture"
        log "  • For non-root packet capture, grant capabilities:"
        log "    sudo setcap cap_net_raw,cap_net_admin+eip $INSTALL_DIR/$BINARY_NAME"
        log ""
        log "  IMPORTANT: File capabilities trigger secure-execution mode in the dynamic"
        log "  loader. Test setcap on the single-file bundle first if you encounter library"
        log "  loading issues."
    fi
    
    log ""
    log "Run '$BINARY_NAME --help' to get started."
}

main "$@"
