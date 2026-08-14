# Releasing Monad

GitHub Releases are the canonical source for every installer and package
manager. A release tag builds six self-contained binaries, checks the finished
archives, publishes their checksums and provenance, then updates Homebrew and
submits the WinGet update.

## One-time channel setup

The release workflow expects:

- `HOMEBREW_TAP_DEPLOY_KEY`: an SSH deploy key with write access only to
  `leduftw/homebrew-tap`. Its public half belongs on that repository; its
  private half is a Monad Actions secret.
- `WINGET_TOKEN`: a classic GitHub token with `public_repo`, used by
  WingetCreate to open update pull requests in `microsoft/winget-pkgs`. The
  workflow skips this channel with a notice until the secret exists.

The first WinGet version is a one-time manual submission. Once
`leduftw.monad` exists in `winget-pkgs`, every later tag updates it
automatically.

Immutable releases are enabled on the Monad repository. Keep that setting on:
after a release is published, its tag and assets are intentionally locked.

## Cut a release

1. Change `<Version>` in `Monad/Monad.csproj` to the next stable semantic
   version and commit the change to `main`.
2. Run:

   ```sh
   ./scripts/release.sh 2.0.1
   ```

The script requires a clean `main`, verifies that it is not behind
`origin/main`, runs the locked restore, formatting check and Release tests,
creates an SSH-signed tag, and atomically pushes `main` with the tag.

The workflow then:

1. repeats tests on macOS, Windows and Linux;
2. builds and smoke-tests arm64 and x64 archives for all three systems;
3. publishes a draft only after all six archives exist;
4. adds `SHA256SUMS` and GitHub build-provenance attestations;
5. publishes the release and updates downstream install sources.

If a build fails, fix it and release a new version. Published assets are not
silently replaced.

## Verify publication

After the workflow is green:

```sh
gh release view --repo leduftw/monad
brew update
brew info leduftw/tap/monad
```

WinGet updates are moderated pull requests and can take longer. Check the URL
printed by the `Submit WinGet update` job before calling that channel current.

## Signing status

Release binaries are self-contained, so users do not install .NET or Xcode.
The macOS files are currently ad-hoc signed and Windows files are unsigned.
Checksums and GitHub provenance protect integrity, but fully frictionless direct
browser downloads still require:

- an Apple Developer ID certificate plus hardened-runtime signing,
  `com.apple.security.cs.allow-jit`, and ZIP notarization; stapling an offline
  ticket would require switching the distribution container to a PKG or DMG;
- an Authenticode certificate for Windows reputation and publisher identity.

Homebrew and the checksum-verifying installers are the preferred paths until
those identities are available. Linux still needs `parec` (installed by the
Homebrew formula as `pulseaudio`) or an existing `ffmpeg`; removing that last
runtime dependency requires a direct Linux audio implementation.

The workflow downloads a pinned WingetCreate release and verifies its SHA-256
before exposing `WINGET_TOKEN` to it. Update the pinned version and checksum
together when intentionally upgrading that publishing tool.
