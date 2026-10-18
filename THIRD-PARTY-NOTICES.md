# Third-Party Notices

RobotStudioMCP is licensed under the MIT License (see `LICENSE`). It depends on
the following third-party packages, each under its own license.

## Runtime dependencies (shipped in the add-in/watchdog)

| Package | License |
| --- | --- |
| [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) | MIT |
| [ModelContextProtocol.AspNetCore](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore) | MIT |
| [System.CommandLine](https://www.nuget.org/packages/System.CommandLine) | MIT |
| [Microsoft.Extensions.DependencyInjection](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection) | MIT |
| [Microsoft.Extensions.Hosting](https://www.nuget.org/packages/Microsoft.Extensions.Hosting) | MIT |

## Build-time / test-only dependencies (not shipped)

| Package | License |
| --- | --- |
| [GitVersion.MsBuild](https://www.nuget.org/packages/GitVersion.MsBuild) | MIT |
| [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk) | MIT |
| [MSTest.TestAdapter](https://www.nuget.org/packages/MSTest.TestAdapter) | MIT |
| [MSTest.TestFramework](https://www.nuget.org/packages/MSTest.TestFramework) | MIT |
| [Moq](https://www.nuget.org/packages/Moq) | BSD-3-Clause |
| [coverlet.collector](https://www.nuget.org/packages/coverlet.collector) | MIT |
| [pytest](https://pypi.org/project/pytest/) (Python dev dependency, scripts/tests only) | MIT |

## Not third-party

`Lagun271.RobotStudio.Sdk.Ref` and `Lagun271.RobotStudio.RspakTool` are first-party packages from a separate feed
(`Lagun271/robotstudio-sdk-nuget`). They provide compile-time reference
assemblies and build tooling only; no ABB RobotStudio binaries are
redistributed by this project.
