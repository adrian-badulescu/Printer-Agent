# WireGuard SSH key for dev API (qrapi-dev-*)

Backend repo: `QR_Restaurant_backend` — `appsettings.DevHost.json` has `PrinterAgent:WireGuard:Ssh:Enabled=true` but **no private key in git**.

Without the key, `GET /api/agents/{id}/wireguard-conf` returns **HTTP 400** and the Windows agent never gets a `.conf` (tunnel is not created).

**Full server setup** (wg0, scripts, `wgctl`, Redis on `wg0`): see [wireguard-ssh-provisioning/README.md](wireguard-ssh-provisioning/README.md). Production hub: [PRODUCTION_HUB.md](wireguard-ssh-provisioning/PRODUCTION_HUB.md).

## 1) Server prerequisites

On `192.168.43.142` (or your dev host), complete [wireguard-ssh-provisioning/README.md](wireguard-ssh-provisioning/README.md) sections 1–4:

- `wg0` up, UDP `51820` open
- `/usr/local/bin/wg-rebuild-generated`, `wg-peer-upsert`, `wg-peer-delete`, `wg-sync-peers` installed (scripts in this repo folder)
- user `wgctl` + sudoers
- SSH keypair: **private** in `/etc/urs/`, **public** in `/home/wgctl/.ssh/authorized_keys`

## 2) Generate SSH keypair (fresh start)

On the dev host, as root:

```bash
sudo mkdir -p /etc/urs

# Private key for API (NOT in /home/wgctl/.ssh/)
sudo ssh-keygen -t ed25519 -a 64 -f /etc/urs/wgctl_wireguard_ed25519 -N "" -C "qr-backend-wireguard"

# Public key → wgctl authorized_keys
sudo mkdir -p /home/wgctl/.ssh
sudo bash -c 'cat /etc/urs/wgctl_wireguard_ed25519.pub >> /home/wgctl/.ssh/authorized_keys'
sudo chown -R wgctl:wgctl /home/wgctl/.ssh
sudo chmod 700 /home/wgctl/.ssh
sudo chmod 600 /home/wgctl/.ssh/authorized_keys

# API runs as www-data — must read private key for manual tests; env file too
sudo chown root:www-data /etc/urs/wgctl_wireguard_ed25519
sudo chmod 640 /etc/urs/wgctl_wireguard_ed25519
```

Verify:

```bash
sudo ssh-keygen -y -f /etc/urs/wgctl_wireguard_ed25519 | awk '{print $2}' | head -c 20
sudo awk '{print $2}' /home/wgctl/.ssh/authorized_keys | head -c 20
# same prefix = OK

sudo -u www-data ssh -i /etc/urs/wgctl_wireguard_ed25519 \
  -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o BatchMode=yes \
  wgctl@127.0.0.1 'echo OK'
```

## 3) Configure the API (one-time)

```bash
sudo bash -c 'base64 -w0 /etc/urs/wgctl_wireguard_ed25519 > /etc/urs/wg-ssh-private-key.b64'

sudo tee /etc/urs/printer-agent-secrets.env >/dev/null <<EOF
PrinterAgent__WireGuard__Ssh__PrivateKeyBase64=$(cat /etc/urs/wg-ssh-private-key.b64)
EOF

sudo chown root:www-data /etc/urs/printer-agent-secrets.env /etc/urs/wg-ssh-private-key.b64
sudo chmod 640 /etc/urs/printer-agent-secrets.env /etc/urs/wg-ssh-private-key.b64

# Must decode to BEGIN OPENSSH PRIVATE KEY — not "ssh-ed25519 AAAA..."
grep PrivateKeyBase64 /etc/urs/printer-agent-secrets.env | cut -d= -f2 | base64 -d | head -1
```

Systemd drop-ins for both API instances:

```bash
for u in qrapi-dev-1 qrapi-dev-2; do
  sudo mkdir -p "/etc/systemd/system/${u}.service.d"
  sudo tee "/etc/systemd/system/${u}.service.d/wireguard-ssh.conf" >/dev/null <<'EOF'
[Service]
EnvironmentFile=/etc/urs/printer-agent-secrets.env
EOF
done
sudo systemctl daemon-reload
sudo systemctl restart qrapi-dev-1 qrapi-dev-2
```

Verify env is loaded by the running process (not `systemctl show -p Environment` alone):

```bash
PID=$(systemctl show qrapi-dev-1 -p MainPID --value)
sudo tr '\0' '\n' < /proc/$PID/environ | grep PrinterAgent__WireGuard__Ssh__PrivateKeyBase64 | cut -d= -f2 | base64 -d | head -1
```

## 4) Verify end-to-end

**Server:**

```bash
journalctl -u qrapi-dev-1 -n 50 --no-pager | grep -i wireguard
# expect: Issued WireGuard config for agentId ... (not WireGuard config rejected)
```

**Windows** (after restarting `URSPrinterAgent`):

- `C:\ProgramData\URSPrinterAgent\wireguard\urs-printer-agent.conf` exists
- `Get-Service 'WireGuardTunnel$urs-printer-agent'` → **Running**
- Agent E2E: [E2E_AGENT_DEPLOYMENT_CHECKLIST.md](E2E_AGENT_DEPLOYMENT_CHECKLIST.md)
