#!/usr/bin/env bash
# Run as root: bash install.sh /absolute/path/to/extracted/linux-x64-package
set -euo pipefail
if [[ $EUID -ne 0 || $# -ne 1 ]]; then echo 'Usage: sudo bash install.sh PACKAGE_DIRECTORY' >&2; exit 2; fi
package=$(realpath "$1")
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
test -f "$package/ResourceManager.Server"
getent group resourcemanager >/dev/null || groupadd --system resourcemanager
id resourcemanager >/dev/null 2>&1 || useradd --system --gid resourcemanager --home-dir /var/lib/resourcemanager --shell /usr/sbin/nologin resourcemanager
install -d -m 750 -o resourcemanager -g resourcemanager /var/lib/resourcemanager
install -d -m 755 /opt/resourcemanager /etc/resourcemanager
if [[ ! -f /etc/resourcemanager/server.json ]]; then
    install -m 640 -o root -g resourcemanager "$script_dir/server.example.json" /etc/resourcemanager/server.json
fi
# Back up stopped data, including SQLite sidecars and server identity. Preserve existing configuration.
if systemctl is-active --quiet resourcemanager; then systemctl stop resourcemanager; fi
backup=/var/backups/resourcemanager/$(date -u +%Y%m%dT%H%M%S)-$$
install -d -m 700 "$backup"
cp -a /var/lib/resourcemanager "$backup/data"
cp -a /etc/resourcemanager "$backup/config"
cp -a /opt/resourcemanager "$backup/program"
rollback() {
    echo "Installation failed. Backup: $backup. Restore program and data before retrying." >&2
    exit 1
}
trap rollback ERR
cp -a "$package/." /opt/resourcemanager/
chmod +x /opt/resourcemanager/ResourceManager.Server
runuser -u resourcemanager -- /opt/resourcemanager/ResourceManager.Server check-config --config /etc/resourcemanager/server.json
runuser -u resourcemanager -- /opt/resourcemanager/ResourceManager.Server doctor --config /etc/resourcemanager/server.json
runuser -u resourcemanager -- /opt/resourcemanager/ResourceManager.Server migrate --config /etc/resourcemanager/server.json
install -m 644 "$script_dir/resourcemanager.service" /etc/systemd/system/resourcemanager.service
systemctl daemon-reload
systemctl enable --now resourcemanager
echo "Installed. Backup: $backup. Check: systemctl status resourcemanager; journalctl -u resourcemanager"
