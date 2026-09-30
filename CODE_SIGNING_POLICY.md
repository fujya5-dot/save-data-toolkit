# Code signing policy

No public OSS Windows release has been published or approved for signing yet. An initial public preview binary, if published before a signing arrangement is approved, will be identified as **unsigned**. Do not interpret an Ed25519 signature on a declarative Demo Pack as Windows Authenticode signing of the application.

Future signing is limited to release binaries generated and owned by YuniWorks, such as the Core application EXE and first-party DLLs. Microsoft/.NET, Windows SDK, CsWinRT, and other upstream binaries retain their upstream signatures and must not be re-signed with a YuniWorks certificate. Data files and declarative packs are not Authenticode targets.

A private signing key must never enter the public repository or build logs. Build provenance, artifact hashes, and signing approval are separate release steps. Any future SignPath use requires its own eligibility decision and manual approval for **each** release. This policy does not claim SignPath approval or an active signing pipeline.

## Project status and responsibilities

Application status: **not yet approved** by SignPath Foundation. The initial planned Windows release is an unsigned portable ZIP. The public GitHub Actions workflow builds it from this repository on a GitHub-hosted Windows runner and uploads the result as a workflow artifact. If signing is approved later, the intended trusted build system is GitHub Actions with origin verification against this public repository and its `main` release branch. A local upload is not the normal release signing path.

The current project has one maintainer, [fujya5-dot](https://github.com/fujya5-dot). Authors/Committers: fujya5-dot. Reviewers: fujya5-dot reviews external contributions; there is no separate reviewer currently. Approvers: fujya5-dot is the intended release decision maker; no SignPath approver role has been configured. Whether one person may hold these roles for a Foundation certificate remains subject to Foundation review. No additional team member or independent approval is claimed.

Only the eight YuniWorks-built application EXE/DLL files listed in `tools/release/windows-authenticode-files.txt` are future signing candidates. Upstream Microsoft/.NET/Windows SDK/CsWinRT EXE/DLL files are included unmodified under their own terms and must not be signed again with a YuniWorks or Foundation certificate. The [Privacy policy](PRIVACY.md) describes local file handling and the absence of automatic data transfer.

The Foundation-required attribution, **“Free code signing provided by SignPath.io, certificate by SignPath Foundation”**, is reserved for a future approved signing arrangement and signed download page. It does **not** describe the current unsigned preview.
