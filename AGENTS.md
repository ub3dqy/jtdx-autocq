# AutoCQ release process

- For a program update, update the version in `src/AutoCqApp.cs` and the Russian and English notes in `RELEASE.md`. Keep `README.md` and `README.en.md` in sync when usage changes.
- Commit the tested update in this checkout on `main`. Run `.\Publish-Release.ps1` with the authorized GitHub CLI, then verify the public release and Windows archive before handing off the update. Pass `-JtdxBin` when the patched JTDX binaries are available for the native tests.
- To build and check the package locally, use `.\Publish-Release.ps1 -PackageOnly`. Release archives contain the module, patch and documentation. JTDX installers are linked from the README.
- Every feature update must include current Russian and English READMEs and user guides, plus fresh genuine demo screenshots of the changed workflows. Use plain conversational language for ordinary operators. Refresh the offline guides and the app's help links as well.
- The normal install and update path must work through the app without requiring a user to type shell commands. Keep technical build instructions separate from the quick start.
- Keep personal profiles, live radio logs, authentication files, user-specific paths and private keys out of the repository, screenshots and release archives. Recheck the public package before publication.
