# Core and Compatibility Packs

Save Data Toolkit Core is free and open source under MPL-2.0 for the YuniWorks-owned files identified below. It is useful without a paid pack: Generic Safe File Mode can inspect an unknown file, show size and SHA-256, create a verified backup, and restore as a new file; the Synthetic Demo exercises the supported editing workflow.

## License boundary

MPL-2.0 covers YuniWorks-owned Core source and the YuniWorks-generated Synthetic Demo generator, identity and save assets, definition, and signed declarative demo pack in the clean public repository. The demo contains synthetic data only: no commercial save, ROM-derived data, paid-pack field definition, or proprietary game data.

MPL-2.0 does not relicense Microsoft or other upstream binaries, Windows/.NET redistributables, third-party materials under their own terms, private commercial research, or separately sold proprietary Compatibility Packs. The presence of a third-party binary beside the Core does not make that binary YuniWorks-owned.

## Pack boundary

A Compatibility Pack is declarative data interpreted by the Core. A paid pack may be distributed separately under proprietary terms. Paid packs are absent from the public Core repository and are not needed to build, test, or run the Core. They contain no executable code, DLL, EXE, script, or runtime component.

The user imports a pack manually. Core does not download, install, or unlock paid packs automatically. Pack signature and trust checks establish publisher provenance and integrity; they do not grant a license or prove purchase entitlement. The Core does not require an account, cloud service, marketplace client, or licensing client for the preview workflow.

The Core has no bundled ROM, BIOS, firmware, commercial game save, or copyrighted game artwork. Users supply their own lawful files for any separately supported game. A pack can expand recognized save fields only after the Core verifies its signature and compatibility; an unsupported file remains read-only.
