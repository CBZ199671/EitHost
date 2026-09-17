# Third-Party Notices

The MIT License in [`LICENSE`](LICENSE) applies to source code and documentation authored for EitHost, except where a file states otherwise.

## USB2070 vendor runtime

The following files are vendor-supplied interoperability components and are **not** covered by the EitHost MIT license:

- `src/EitHost.App/Native/x64/USB2070.dll`
- `release/EitHost-Windows-x64/USB2070.dll`

All rights and redistribution terms for these files remain with their respective copyright holder. The USB2070 Windows device driver is not included in this repository and must be obtained from the hardware vendor.

## Self-contained Windows release

`release/EitHost-Windows-x64/EitHost.App.exe` is a self-contained build. In addition to EitHost code, it contains Microsoft .NET runtime components and third-party dependencies. Those components remain subject to their own license terms and are not relicensed under MIT by this repository.

## NuGet dependencies

The dependencies declared in the project files, including HDF.PInvoke, Microsoft.Data.Sqlite, PureHDF, SQLitePCLRaw, System.IO.Ports, and System.Management, remain subject to their respective upstream licenses.

This notice is informational and does not replace the license text distributed by an upstream project or vendor.
