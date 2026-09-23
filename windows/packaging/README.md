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

If Windows reports `0x800B0109`, import the `.cer` path printed by
`new-dev-certificate.ps1` into the current user's Trusted Root Certification
Authorities store, then run the registration script again.

The package is signed with a local development certificate. No generated
certificate or MSIX belongs in Git.
