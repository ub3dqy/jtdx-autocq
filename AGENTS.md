# AutoCQ release process

- For a program update, update the version in `src/AutoCqApp.cs` and the Russian and English notes in `RELEASE.md`. Keep `README.md` and `README.en.md` in sync when usage changes.
- Commit the tested update in this checkout on `main`. Run `.\Publish-Release.ps1` with the authorized GitHub CLI, then verify the public release and Windows archive before handing off the update. Pass `-JtdxBin` when the patched JTDX binaries are available for the native tests.
- To build and check the package locally, use `.\Publish-Release.ps1 -PackageOnly`. Release archives contain the module, patch and documentation. JTDX installers are linked from the README.
