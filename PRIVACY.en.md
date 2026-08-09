# Windows Dynamic Capsule Privacy Policy

[简体中文](PRIVACY.md) | **English** | [繁體中文](PRIVACY.zh-TW.md)

Effective date: August 9, 2026

Windows Dynamic Capsule is a desktop utility that runs locally on a Windows
PC. This policy explains what data the app processes, why it is processed, and
the controls available to the user.

## Data processed locally

The app may process the following information on the device:

- the title, artist, album, playback state, duration, and artwork from the
  current media session;
- the source, title, and body of Windows toast notifications after the user
  grants permission;
- task names, states, and progress sent by the user to the local named pipe;
- the display name and connection state of paired Bluetooth devices, and the
  current Wi-Fi profile name or SSID, to show brief connection, disconnection,
  and network-switch events;
- timer state, display selection, privacy level, notification filters, and
  other app settings.

Notification, task, and connectivity content is used only for temporary
display and is not written to the settings file. Synchronized lyrics may be cached in
`%LOCALAPPDATA%\WindowsDynamicCapsule\lyrics-cache`. Cache file names are
derived from hashes of song information and do not contain song titles
directly. Artwork is not written to this cache. App settings are stored in
`%LOCALAPPDATA%\WindowsDynamicCapsule\settings.json` for the current user.

## Lyrics lookup

To find synchronized lyrics, the app may send the current song title, artist,
album name, and duration over HTTPS to `https://lrclib.net`. Windows
notification content, local task content, and app settings are not sent to
LRCLIB. LRCLIB processes requests under its own privacy policy and terms.
Bluetooth device names, Wi-Fi names, and SSIDs are not sent to lyrics services
or other third parties.

If the user enables the optional QQ Music fallback, the app may send the song
title, artist, and required resource identifiers to `c.y.qq.com` and
`y.gtimg.cn` when LRCLIB has no suitable synchronized lyrics. This is used to
match lyrics and artwork. The fallback can be disabled at any time; lyrics
already cached locally remain available offline.

## Data the app does not collect

The app contains no advertising or behavioral telemetry. It does not create
user profiles, sell or rent personal data, upload notification, task,
Bluetooth-device, or Wi-Fi content, or collect passwords, access tokens,
clipboard contents, or precise location.

## User controls

Users can:

- deny notification access or revoke it later in Windows Settings;
- configure notification allowlists, blocklists, and privacy display levels;
- enable the capsule's Do Not Disturb mode;
- disable the QQ Music fallback source;
- control launch at sign-in from Windows Startup Apps or Task Manager;
- delete the local settings file and `lyrics-cache` folder, or uninstall the
  app to stop further processing.

## Retention and security

The app does not maintain a database of notification, task, or connectivity
content.
Synchronized lyrics are cached for 30 days, with a maximum of 256 songs; the
oldest entries are removed first when the limit is exceeded. Settings and
cache files remain in the current user's local app-data directory. The app
uses a current-user-only local communication channel and does not listen on a
network port.

## Children's privacy

The app is not directed at collecting personal data from children and does
not require an account.

## Changes to this policy

If the app's data handling changes materially, this policy will be updated
with the app version and the effective date above will be revised.

## Contact

For privacy questions, use the repository's private security reporting
feature. If that feature is unavailable, open a public issue that contains no
notification text, logs, tokens, or other personal data and ask the maintainer
to establish a private communication channel.
