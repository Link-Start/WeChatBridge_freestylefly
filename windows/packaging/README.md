# Windows sparse MSIX

The package in `SparsePackage/` carries package identity and Share Target
registration. The executables remain outside the package:

```text
<install-root>/WeChatBridge.Windows.exe
<install-root>/share-target/WeChatBridge.ShareTarget.exe
```

For local development:

1. Run `scripts/new-dev-certificate.ps1`.
2. Publish the WPF app and helper into the layout above.
3. Run `scripts/register-dev.ps1 -InstallRoot <install-root>`.

Before registration, import the `.cer` path printed by
`new-dev-certificate.ps1` into the **Local Computer > Trusted People** store
with administrator rights. Then run the registration script.

The package is signed with a local development certificate. No generated
certificate or MSIX belongs in Git.

For a release or tester build, use the repository workflow
`.github/workflows/windows-sign.yml` with SignPath Foundation. Configure the
SignPath organization, project, signing policy, API token, and the exact MSIX
publisher subject as repository variables and secrets before dispatching it.
