# Contributing

The initial OSS preview feature scope is frozen. Please open an issue describing a reproducible Core defect or a narrowly scoped documentation improvement before proposing a larger change. Use this repository's issue tracker and pull requests.

Build requirements and commands are in [Building](docs/BUILDING.md). Run the checks relevant to a change and report exact results, including anything not run. Verification-only and commercial material is not an acceptable public test fixture. The Core-only test entry is `tests/YuniRetroToolkit.PublicTests`.

Changes to file parsing, pack trust/signatures, backup, export, or rollback need focused security review and meaningful tests. Preserve original-file immutability, explicit validation, and fail-closed behavior. Keep paid-pack entitlement and proprietary game definitions out of the free Core.

Do not contribute secrets, credentials, personal paths, ROMs, BIOS/firmware, commercial save dumps, game artwork, old-tool code or internal tables, or proprietary Compatibility Pack data. Use synthetic examples you created and may license for inclusion. By submitting a contribution, you confirm you have the rights needed to offer it under the repository's MPL-2.0 license; retain applicable third-party copyright and license notices.
