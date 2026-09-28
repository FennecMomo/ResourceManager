#!/usr/bin/env bash
# Run as root: bash install.sh /absolute/path/to/extracted/linux-x64-package
set -euo pipefail
step=preflight
trap 'printf "{\"ok\":false,\"code\":\"INSTALL_STEP_FAILED\",\"step\":\"%s\"}\n" "$step" >&2' ERR
command -v python3 >/dev/null
command -v curl >/dev/null
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
step=backup
python3 "$script_dir/maintenance.py" backup --backup "$backup"
step=install
cp -a "$package/." /opt/resourcemanager/
chmod +x /opt/resourcemanager/ResourceManager.Server
runuser -u resourcemanager -- /opt/resourcemanager/ResourceManager.Server check-config --config /etc/resourcemanager/server.json
runuser -u resourcemanager -- /opt/resourcemanager/ResourceManager.Server doctor --config /etc/resourcemanager/server.json
runuser -u resourcemanager -- /opt/resourcemanager/ResourceManager.Server migrate --config /etc/resourcemanager/server.json
install -m 644 "$script_dir/resourcemanager.service" /etc/systemd/system/resourcemanager.service
systemctl daemon-reload
step=start
systemctl enable --now resourcemanager
step=health
api_port=$(python3 -c 'import json; print(json.load(open("/etc/resourcemanager/server.json")).get("api-port",37644))')
for attempt in {1..30}; do
    if curl --silent --fail "http://127.0.0.1:$api_port/health/ready" >/dev/null; then
        printf '{"ok":true,"code":"INSTALL_READY","backup":"%s"}\n' "$backup"
        exit 0
    fi
    sleep 1
done
echo '{"ok":false,"code":"HEALTH_TIMEOUT"}' >&2
exit 3
