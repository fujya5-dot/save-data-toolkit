# Code-signing policy

No public OSS Windows release has been published or approved for signing yet. An initial public preview binary, if published before a signing arrangement is approved, will be identified as **unsigned**. Do not interpret an Ed25519 signature on a declarative Demo Pack as Windows Authenticode signing of the application.

Future signing is limited to release binaries generated and owned by YuniWorks, such as the Core application EXE and first-party DLLs. Microsoft/.NET, Windows SDK, CsWinRT, and other upstream binaries retain their upstream signatures and must not be re-signed with a YuniWorks certificate. Data files and declarative packs are not Authenticode targets.

A private signing key must never enter the public repository or build logs. Build provenance, artifact hashes, and signing approval are separate release steps. Any future SignPath use requires its own eligibility decision, trusted build setup, and manual approval for each release. This policy does not claim SignPath approval or an active signing pipeline.
