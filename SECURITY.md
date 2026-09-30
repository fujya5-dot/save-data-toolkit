# Security policy

## Supported scope

Security reports for the initial OSS Core preview should concern the Windows desktop Core, Generic Safe File Mode, synthetic demo, pack validation and trust boundary, backup/restore, or safe edit/export workflow. Paid Compatibility Packs and private verification tools are outside this public repository.

The Core opens original saves read-only, works on a copy, checks declared structure and integrity, creates backups, and exports to a new file. Unknown or unsupported files remain read-only. A signature is checked before a declarative pack is trusted. These safeguards reduce risk but cannot guarantee that an arbitrary save is valid or that editing has no consequences.

## Reporting a vulnerability

Use this repository's **Security → Report a vulnerability** private reporting option if enabled. Do not include exploit details, personal saves, ROMs, credentials, or private data in a public issue. If private reporting is unavailable, open a public issue containing only a request for a private contact channel, without technical exploit details. This document does not claim that a private reporting channel or security email already exists.

Please include the affected Core version, a minimal synthetic reproducer, impact, and safe reproduction steps. Give maintainers a reasonable opportunity to investigate before public disclosure. Security-sensitive changes to pack verification, file handling, backup, or release integrity require focused review and tests.
