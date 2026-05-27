#!/bin/bash
# Installs + starts egs-openstack-sim as a systemd service on the simulator
# catlet. Uploaded and run by OpenStack.E2E.Tests.ps1 (avoids passing a
# multi-line script as an ssh argument). Runs as root via egs.
set -e
chmod +x /opt/egs-sim/egs-openstack-sim
cat >/etc/systemd/system/egs-openstack-sim.service <<'UNIT'
[Unit]
Description=egs OpenStack metadata simulator
After=network-online.target
Wants=network-online.target
[Service]
ExecStart=/opt/egs-sim/egs-openstack-sim --root /opt/egs-sim-data --prefix http://+:80/
Restart=always
[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable --now egs-openstack-sim.service
sleep 2
systemctl is-active egs-openstack-sim.service
