#!/usr/bin/env bash
# Run as root: maintain.sh backup|restore /absolute/backup/directory
set -euo pipefail
step=arguments
trap 'printf "{\"ok\":false,\"code\":\"MAINTENANCE_STEP_FAILED\",\"step\":\"%s\"}\n" "$step" >&2' ERR
[[ $EUID -eq 0 && $# -eq 2 && ( $1 == backup || $1 == restore ) ]] || { echo 'Usage: sudo bash maintain.sh backup|restore BACKUP_DIRECTORY' >&2; exit 2; }
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
step=stop
was_active=false
if systemctl is-active --quiet resourcemanager; then was_active=true; systemctl stop resourcemanager; fi
step=$1
python3 "$script_dir/maintenance.py" "$1" --backup "$2"
step=start
if [[ $was_active == true ]]; then systemctl start resourcemanager; fi
printf '{"ok":true,"code":"MAINTENANCE_FINISHED","action":"%s"}\n' "$1"
