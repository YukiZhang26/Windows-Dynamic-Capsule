# SignPath Foundation application preparation

Status: **preparation only**. Windows Dynamic Capsule has not been accepted by
SignPath Foundation and does not currently claim a Foundation certificate.

## Project

- Name: Windows Dynamic Capsule
- Repository: <https://github.com/YukiZhang26/Windows-Dynamic-Capsule>
- License: MIT
- Maintainer: [Yuki Zhang](https://github.com/YukiZhang26)
- Public preview: [v0.1.0-preview.1](https://github.com/YukiZhang26/Windows-Dynamic-Capsule/releases/tag/v0.1.0-preview.1)
- Privacy policy: [PRIVACY.en.md](PRIVACY.en.md)
- Code signing policy: [CODE_SIGNING.md](CODE_SIGNING.md)

Windows Dynamic Capsule is a Windows 10/11 desktop status surface for media,
synchronized lyrics, user-authorized notifications, local task and download
progress, timers, stopwatches, and local Bluetooth/Wi-Fi connection events.
It is a WPF application distributed as a self-contained x64 build.

The app runs at medium integrity. It does not request administrator elevation,
install a service or driver, modify protected system files, execute downloaded
code, expose a network listener, contain advertising, or include behavioral
telemetry. Network requests are limited to the lyric providers disclosed in
the privacy policy. Notification, task, Bluetooth-device, and Wi-Fi content is
not uploaded.

## Foundation eligibility evidence

| Requirement | Repository evidence | Status |
| --- | --- | --- |
| OSI-approved license | `LICENSE` (MIT) | Ready |
| Public source and build scripts | Public GitHub repository | Ready |
| Documented behavior | Three README files and `PROJECT.md` | Ready |
| Public release | Portable preview release with digest and source commit | Ready |
| Privacy disclosure | Three privacy policies; network endpoints audited in CI | Ready |
| Installation and removal | Portable and MSIX removal instructions in all READMEs | Ready |
| Signing roles | Committer/reviewer/approver listed in `CODE_SIGNING.md` | Ready |
| Verifiable build | GitHub-hosted Windows workflow, pinned SDK, clean-tree provenance | Ready |
| Sign only project-owned binaries | EXE, project DLL, and enclosing MSIX only | Ready |
| Fixed product metadata | Product name and product/file versions constrained in XML | Ready |
| Maintained/reputation | New project with active CI and preview release | Review by Foundation |
| Release in target MSIX form | Store-signed 1.0 should be published first | Pending Store certification |

The proposed sequence is therefore:

1. complete Microsoft Store certification for the Store-identity `1.0.0.0`
   package;
2. publish the Store-signed product and verify installation/uninstallation;
3. apply to SignPath Foundation for the separate direct-download identity;
4. configure SignPath origin verification and manual signing approval;
5. run the repository's manual trusted-signing workflow from the reviewed
   `v1.0.0` tag;
6. publish only the MSIX for which
   `verify-direct-release-msix.ps1` returns
   `PublicReleaseEligible=True`.

## Proposed signing configuration

- Trusted build system: predefined GitHub.com connector.
- Runner: GitHub-hosted `windows-latest` only.
- Project slug: to be assigned after acceptance.
- Signing policy: release signing with origin verification and manual approval.
- Artifact configuration: `packaging/signpath-artifact-configuration.xml`.
- Signed files: `WindowsDynamicCapsule.exe`,
  `WindowsDynamicCapsule.dll`, and the enclosing direct-channel MSIX.
- Included but not project-signed: Microsoft .NET runtime and third-party
  framework binaries.
- Product metadata restrictions: product name `Windows Dynamic Capsule`,
  product version `1.0.0`, file version `1.0.0.0`, company `Yuki Zhang`.
- Package identity: separate from the Partner Center identity; the manifest
  Publisher will exactly match the accepted signing certificate subject.

Every request must originate from a GitHub-hosted build of the reviewed tag,
use the checked-in version and artifact configuration, and receive manual
approval. Re-runs are not used to sign an old build as a current release.

## Values required after acceptance

The following values belong in GitHub repository variables, except for the API
token, which belongs in GitHub Actions secrets:

- `SIGNPATH_ORGANIZATION_ID`
- `SIGNPATH_PROJECT_SLUG`
- `SIGNPATH_SIGNING_POLICY_SLUG`
- `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`
- `DIRECT_MSIX_IDENTITY`
- `DIRECT_MSIX_PUBLISHER`
- `DIRECT_MSIX_PUBLISHER_DISPLAY_NAME`
- secret `SIGNPATH_API_TOKEN`

The SignPath GitHub App must be granted access to this repository. None of
these values are invented before acceptance, and the workflow never falls back
to a self-signed certificate.

## Required attribution after acceptance

Planned attribution: **Free code signing provided by
[SignPath.io](https://signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).** This sentence documents the
required future attribution and is not a claim that the project is currently
accepted or signed.

Official references:

- <https://signpath.org/terms.html>
- <https://docs.signpath.io/trusted-build-systems/github>
- <https://docs.signpath.io/artifact-configuration/reference>
