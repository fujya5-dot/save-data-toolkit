# Privacy

The OSS Core preview processes selected files locally. It has no login, cloud synchronization, or implemented telemetry/analytics client. The Core does not automatically upload ROMs, saves, packs, or diagnostic reports to YuniWorks.

The application reads the file you select, may create local backups and working copies, and writes a new output file only when you ask it to. It also stores local settings and diagnostic logs needed for operation. You control those files through your local system; opening a file does not send its contents to a service.

A diagnostic report is created only after you choose a new local destination. Its current format contains limited version and status information and excludes save contents, ROM contents, raw personal paths, Windows user name, and secrets. Review any file before sharing it with another person. Reports are not uploaded by the application.

This describes the present Core implementation, not the behavior of separately installed third-party software, a future distribution channel, or a user's own file-sharing choices. See [Security](SECURITY.md) for vulnerability reporting.
