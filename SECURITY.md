# Security

GeuzaVirt handles administrator and root credentials and copies whole machines, so security
reports are welcome.

## Reporting a vulnerability

Please do **not** open a public issue. Email **info@mtaalamtech.com** with "GeuzaVirt security"
in the subject, a description and, if possible, steps to reproduce. We aim to reply within
5 working days.

## How credentials and data are handled

- **Passwords:** they are kept in memory only. They go to `ssh.exe` through `SSH_ASKPASS`, never
  on a command line, and are not saved in settings or logs. Agent pairing keys are remembered,
  encrypted with Windows DPAPI for the current user.
- **The agent:** it listens on TCP 8786. Every request is HMAC-signed with a pairing key over a
  one-time nonce.
- **Disk and file streams:** these are encrypted with AES-256-CTR, using keys derived per session
  and per disk.
- **Linux sources:** a temporary SSH key is added for root and removed after the copy.
- **Proxmox VE sources:** the temporary key may run only the one `vzdump` command.
- **VSS snapshots:** they are deleted when the copy ends. The agent removes any that are left
  over after 48 hours.
- **After a migration:** uninstall the agent with `agent\Uninstall-Agent.cmd` or
  `MtaalamV2VAgent.exe /uninstall`.
