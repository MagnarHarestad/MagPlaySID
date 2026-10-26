# MSIX package for the Microsoft Store

The Store signs what it publishes, so a copy from the Store starts without SmartScreen's
"Windows protected your PC" and without Smart App Control blocking it. No certificate of our own
is needed for that.

```powershell
.\packaging\msix\build-msix.ps1
```

This builds `out\MagSID-Editor-<version>-x64.msix`. The editor is published self-contained, so
.NET is inside the package and nothing else has to be installed.

## Before the first submission

1. Create the developer account at [Partner Center](https://partner.microsoft.com/dashboard/home).
   It is free for individuals; it asks for an ID and a selfie.
2. Reserve the name **MagSID Editor** for a new app.
3. On the app's **Product management → Product identity** page, copy the three values into
   `store-identity.json` next to this file:

   ```json
   {
     "Name": "Package/Identity/Name",
     "Publisher": "Package/Identity/Publisher",
     "PublisherDisplayName": "Package/Properties/PublisherDisplayName"
   }
   ```

   They are not secret - every Store package shows them - so the file can be committed.
4. Build, then upload the `.msix` in a new submission.

Without `store-identity.json` the package gets a test identity that the Store refuses; that is
fine for trying it out locally.

## Trying the package before it is sent

Windows only installs signed packages. Two ways to try it here without a Store signature:

- **Developer Mode** (Settings → System → For developers): register the unpacked layout, no
  signature needed:

  ```powershell
  Add-AppxPackage -Register .\packaging\msix\out\layout\AppxManifest.xml
  ```

  It appears in the Start menu as MagSID Editor; remove it in Settings → Apps.
- **A test certificate**: sign with `-CertificatePfx` and trust that certificate on the machine.
  Its subject has to be the manifest's Publisher.

Things to check in the packaged editor: settings and autosave are kept between runs (Windows keeps
a packaged app's AppData in a folder of the package's own), and **Open bank folder** opens the
folder the editor reads - see `PackagedApp`.

## Versions

The package version is the editor's `<Version>` as four numbers: 1.63 is 1.63.0.0. Every Store
update has to be a higher version than the last, so after 1.63 comes 1.64 or 2.0 - never 1.7.
