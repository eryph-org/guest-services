#!/usr/bin/env bash
set -euo pipefail

if [[ $EUID -ne 0 ]]; then
    echo "This script must be run as root." >&2
    exit 1
fi

script_directory="$(cd "$(dirname "$0")" && pwd)"

mkdir -p /opt/eryph/guest-services
tar -xzf "$script_directory"/*.tar.gz -C /opt/eryph/guest-services
cp "$script_directory/eryph-guest-services.service" /etc/systemd/system/eryph-guest-services.service

systemctl daemon-reload
systemctl enable eryph-guest-services.service
systemctl start eryph-guest-services.service
