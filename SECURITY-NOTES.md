# TCP Tunnel Client Security Notes

This client uses `frpc` (Fast Reverse Proxy) as its tunnel engine.

To reduce false positives and improve transparency:

- `frpc.exe` is **not embedded** in `TCP-Tunnel.exe`.
- The client downloads the official FRP release archive on demand.
- The archive and extracted executable are verified by SHA256 before use.

Current pinned FRP version:

- Version: `0.69.0`
- Archive: `frp_0.69.0_windows_amd64.zip`
- Archive SHA256: `0e38f6dbe7761d648ca5c6ee323b7309544f48c01e9476f553902f3bc0949089`
- `frpc.exe` SHA256: `f8467a4f8d57cde5ba808a764b528147acd81db0955e51bee80fde0fea0e5243`

Download sources:

- `https://github.com/fatedier/frp/releases`
- `https://sourceforge.net/projects/frp.mirror/files/`

Notes:

- Some AV vendors classify FRP tools as `Riskware/NetTool` by policy.
- This classification can happen even for clean builds without malicious code.
- Without code-signing, reputation-based engines may still flag new installers.
