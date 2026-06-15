# TCP Tunnel Public

<p align="center">
  <img src="ui/logo.png" alt="TCP Tunnel Public" width="128" />
</p>

<p align="center">
  A cleaned public GitHub edition of the desktop FRP launcher.<br />
  Private production integrations, account flows, custom protections and update channels are removed.
</p>

## What this public build includes

- Generic FRP desktop launcher for Windows
- Quick connection string import
- Raw FRP TOML import
- Local profile storage
- Local launch and runtime logging
- Self-contained Windows release build

## What was removed on purpose

- Private account and authorization tabs
- Private API or updater integration
- Custom production-only connection protocol
- Private domain coupling
- Private keys, tokens and server-side integrations
- Direct access to the private `tcptunnel.shop` infrastructure

This public build is intentionally blocked from connecting to the private TCP Tunnel production endpoints.

## Quick connection string format

Use a plain query-style string:

```text
host=example.com&server_port=7000&remote_port=6000&local_port=8080&protocol=tcp&name=Demo%20Profile
```

Supported keys:

- `host`
- `server_port`
- `remote_port`
- `local_port`
- `protocol`
- `name`

Accepted `protocol` values:

- `tcp`
- `udp`
- `tcp_udp`

Private keys such as `user`, `auth_token`, `login_token`, `auth.login_token` and `expires_at` are ignored in the quick importer.

## Raw config format

The client also accepts a regular FRP TOML config.

```toml
# profile_name = "Demo Profile"
# tunnel_type = "tcp_udp"
# expires_at = "2027-01-01T00:00:00Z"
serverAddr = "example.com"
serverPort = 7000
loginFailExit = false
transport.protocol = "tcp"
transport.tls.enable = true

[[proxies]]
name = "demo-tcp"
type = "tcp"
localIP = "127.0.0.1"
localPort = 8080
remotePort = 6000
transport.useEncryption = true
transport.useCompression = false

[[proxies]]
name = "demo-udp"
type = "udp"
localIP = "127.0.0.1"
localPort = 8080
remotePort = 6000
```

Supported local metadata tags:

- `# profile_name = "..."`
- `# tunnel_type = "..."`
- `# expires_at = "..."`

These tags are used only by the local UI and are stripped before runtime if needed.

## Build requirements

- Windows 10 or newer
- .NET 8 SDK
- NSIS if you want to build the installer

## Build from source

```powershell
dotnet restore
dotnet build -c Release
```

## Build the portable public release

```powershell
dotnet publish .\TcpTunnelPublic.csproj -c Release -r win-x64 --self-contained true
```

Published output will be created in:

```text
bin\Release\net8.0-windows\win-x64\publish
```

## Build the installer

If NSIS is installed:

```powershell
"C:\Program Files (x86)\NSIS\makensis.exe" .\installer.nsi
```

The installer will be created in:

```text
release\TCP-Tunnel-Public-Setup-1.0.0.exe
```

## How to use

1. Launch `TCP-Tunnel-Public.exe`.
2. Paste either a quick connection string or a full FRP TOML config.
3. Import it into the local profile list.
4. Select the profile.
5. Click `Connect selected`.

## Local files

The app stores local data here:

```text
%AppData%\TCP Tunnel Public
```

That folder contains:

- `profiles.json`
- `runtime\`
- `logs\`
- `exports\`

## GitHub upload checklist

1. Create a new empty GitHub repository.
2. Copy this folder into your git working directory.
3. Initialize git:

```powershell
git init
git branch -M main
git add .
git commit -m "Initial public release"
```

4. Add your remote:

```powershell
git remote add origin https://github.com/your-name/your-repo.git
git push -u origin main
```

5. Upload the built portable release or installer to GitHub Releases.

## Suggested GitHub release assets

- `TCP-Tunnel-Public-Setup-1.0.0.exe`
- `TCP-Tunnel-Public.exe` or the full `publish` folder zipped

## Creator

Frizzy
