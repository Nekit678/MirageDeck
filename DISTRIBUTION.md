# MirageDeck — development/CI package

This Windows 11 x64 package was built by GitHub Actions for development and
testing. It is not a production-ready public release.

## Before installation

The package contains a user-mode UMDF 2 virtual HID driver. It does not contain
a MirageDeck kernel-mode `.sys` driver. Its temporary self-signed certificate
is unique to this CI build, and only the public `.cer` is included. The private
key is deleted from the runner after packaging.

Installing the package adds that public certificate to the local machine
`Root` and `TrustedPublisher` stores. Review the displayed subject, issuer,
expiry, and SHA-256 fingerprint before confirming. Install only artifacts from
trusted runs of the official MirageDeck GitHub workflow. Packages obtained from
mirrors or untrusted workflow runs must not be installed. Enterprise WDAC/App
Control policies may still block the package.

## Installation

1. Extract the ZIP completely into a dedicated directory.
2. Open an elevated PowerShell window in the extracted directory.
3. Verify, review, and install:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   .\scripts\verify-package.ps1
   .\scripts\install-driver.ps1 -DriverDirectory .\driver
   ```

   The installer performs the full verification again before changing the
   system. If certificate trust must be added, it shows the certificate
   identity and SHA-256 fingerprint and continues only after you type
   `INSTALL` exactly.

4. Start `panel\MirageDeck.exe`, wait for `ГОТОВО` and
   `HID 5548:1021 (Global) готов к работе`, then start Stream Dock. If the
   installer requests a restart, restart Windows before opening the panel.

## Removal

Run from the same extracted package so the exact certificate thumbprint in
`PACKAGE-INFO.json` is available:

```powershell
.\scripts\uninstall-driver.ps1
```

The script removes the virtual device and only that exact certificate from
`Root` and `TrustedPublisher`. It does not remove certificates merely because
their subjects are similar. Missing objects are reported as already removed.

Use `-KeepTestCertificate` to keep the certificate intentionally. The driver
package remains in the Driver Store unless its removal is explicitly requested:

```powershell
.\scripts\uninstall-driver.ps1 -RemoveDriverPackage
```

## Security, signing, and legal notice

Checksums, package metadata, build commit/run information, Authenticode signer
matching, expected file names and versions, and checks for private keys and
unexpected executables are handled by `verify-package.ps1`. Test Mode does not
need to be enabled and Secure Boot does not need to be disabled for this UMDF 2
package.

A public release that does not add a custom root certificate must use the
current applicable Microsoft Hardware Developer Program signing path. The
eligibility and publication rules for attestation and HLK/WHQL signing must be
checked against current Microsoft documentation before each release.

MirageDeck is an independent interoperability project and is not affiliated
with, authorized by, endorsed by, or sponsored by Mirabox, HOTSPOTEK, Microsoft,
or USB-IF. Emulated N4 Pro identifiers are used only where necessary for
software compatibility and do not indicate USB-IF certification or ownership
of the corresponding VID/PID.

Third-party notices and license texts are included in
`THIRD_PARTY_NOTICES.md`, `LICENSE`, and `driver/LICENSE-MS-PL`.
