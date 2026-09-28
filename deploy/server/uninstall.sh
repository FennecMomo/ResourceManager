#!/usr/bin/env bash
# Remove only service registration. Keep program, configuration and every data file.
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo 'Run as root.' >&2; exit 2; }
trap 'echo "{\"ok\":false,\"code\":\"UNINSTALL_FAILED\"}" >&2' ERR
if systemctl cat resourcemanager >/dev/null 2>&1; then
    systemctl disable --now resourcemanager
fi
if [[ -f /etc/systemd/system/resourcemanager.service ]]; then
    rm -- /etc/systemd/system/resourcemanager.service
fi
systemctl daemon-reload
echo '{"ok":true,"code":"SERVICE_REMOVED_DATA_PRESERVED"}'
