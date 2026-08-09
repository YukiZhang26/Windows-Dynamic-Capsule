# Code signing policy

Windows Dynamic Capsule has two separate distribution channels. A package from
one channel must not be presented as an update for the other channel.

## Microsoft Store channel

- The Store candidate uses the exact identity assigned in Partner Center.
- Only the unsigned `1.0.0.0` Store candidate is uploaded to Partner Center.
- Microsoft validates and re-signs the MSIX after certification.
- The unsigned candidate and locally test-signed packages are never attached to
  a public GitHub Release.
- Store-managed installation and updates are the preferred end-user path.

## Direct GitHub channel

A public direct-download MSIX requires a certificate that chains to a Windows
trusted root. Its manifest `Publisher` must exactly match that certificate's
subject. Because the Partner Center Publisher and a third-party signing
certificate normally have different subjects, the direct channel requires its
own stable package identity and upgrade chain.

No direct-download binary will be published until all of the following are true:

1. The artifact is built from a reviewed tag on this repository by a hosted CI
   runner.
2. Release build, CoreProbe, repository verifiers, MSIX verification, and WACK
   all pass.
3. The package and the project's executable are signed by an approved public
   code-signing provider and have a trusted timestamp.
4. `scripts/verify-direct-release-msix.ps1` reports
   `PublicReleaseEligible=True` with the expected identity, Publisher, version,
   and tag.
5. A maintainer manually approves the signing request and the GitHub Release.

Self-signed development certificates, locally trusted certificates, unsigned
MSIX files, and Partner Center upload candidates are not public release assets.

For local upgrade testing only, `build-msix.ps1` accepts
`-AllowUntrustedDevelopmentCertificate`. This does not disable signature
verification: the application binaries and MSIX must still use Authenticode,
match the requested certificate thumbprint, and contain the required RFC 3161
timestamp. The only tolerated verification failure is a certificate chain with
exactly one `UntrustedRoot` status. Hash mismatches, missing signatures, wrong
signers, expired certificates, additional chain errors, and missing timestamps
remain fatal. `verify-direct-release-msix.ps1` never enables this exception.

## Provider status

Microsoft Store signing is the active production path. For a future trusted
GitHub package, the project will use one of these options:

- an approved SignPath Foundation open-source subscription; or
- a publicly trusted OV/EV code-signing certificate obtained by the maintainer.

Microsoft Artifact Signing Public Trust is only an option when the maintainer
meets Microsoft's current country/region eligibility. A private-trust profile
does not make a public download generally trusted.

SignPath supports MSIX and GitHub-origin verification, but the project must be
accepted before its service or certificate may be claimed. If accepted, this
document and the release page will be updated with SignPath's required
attribution and the final team-role configuration.

The proposed deep-signing definition is kept in
[`packaging/signpath-artifact-configuration.xml`](packaging/signpath-artifact-configuration.xml).
It signs only the project's EXE, the project's DLL, and the enclosing MSIX.
Bundled .NET and third-party binaries are not signed as if the project owned
them. The manifest Publisher must be changed to the exact accepted certificate
subject before the first direct-channel package is built.

## Team roles

- Committer and reviewer: [Yuki Zhang](https://github.com/YukiZhang26)
- Release and signing approver: [Yuki Zhang](https://github.com/YukiZhang26)

Repository and signing-provider accounts used for releases must have
multi-factor authentication enabled. Signing credentials and API tokens are
never committed to the repository or exposed to pull-request workflows.

## Privacy and provenance

The application's data handling is documented in
[PRIVACY.en.md](PRIVACY.en.md). The app does not upload notification, task,
Bluetooth-device, or Wi-Fi content. Network access for lyrics is user-triggered
by media playback and is described in the privacy policy.

Every public binary release will include a SHA-256 digest and a link to the
source tag and CI run from which it was built.
