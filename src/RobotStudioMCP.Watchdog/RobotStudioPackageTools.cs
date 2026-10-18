using System.ComponentModel;
using System.IO.Compression;
using ModelContextProtocol.Server;

namespace RobotStudioMCP.Watchdog;

[McpServerToolType]
public sealed class RobotStudioPackageTools
{

  private static string? DefaultUserDistributionPackagesRoot
  {
    get
    {
      var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      return string.IsNullOrWhiteSpace(localAppData) ? null : Path.Combine(localAppData, "ABB", "DistributionPackages");
    }
  }

  private static string? DefaultUserDistributionPackagesRoot2
  {
    get
    {
      var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      return string.IsNullOrWhiteSpace(localAppData) ? null : Path.Combine(localAppData, "ABB", "DistributionPackages2");
    }
  }

  private static string GetDefaultDistributionPackagesRoot()
  {
    return DefaultUserDistributionPackagesRoot2
        ?? DefaultUserDistributionPackagesRoot
        ?? string.Empty;
  }

  [McpServerTool(Destructive = true, Title = "Install RobotStudio .rspak package"), Description("Install a RobotStudio .rspak by extracting it into the RobotStudio distribution package root.")]
  public static object InstallRspakPackage(
      [Description("Full path to .rspak package file.")] string rspakPath,
      [Description("Optional override for RobotStudio distribution package root. Default: %LOCALAPPDATA%\\ABB\\DistributionPackages2")] string? distributionPackagesRoot = null,
      [Description("If true, overwrite (delete and re-extract) an existing package directory with the same RootDir. Defaults to false so a stray call can't silently discard an existing install.")] bool overwriteExisting = false,
      [Description("If true, only report intended actions without writing changes.")] bool dryRun = false)
  {
    var resolvedRspakPath = (rspakPath ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(resolvedRspakPath))
    {
      return new
      {
        installed = false,
        error = "The 'rspakPath' argument is required."
      };
    }

    var root = string.IsNullOrWhiteSpace(distributionPackagesRoot)
        ? GetDefaultDistributionPackagesRoot()
        : distributionPackagesRoot.Trim();

    if (string.IsNullOrWhiteSpace(root))
    {
      return new
      {
        installed = false,
        error = "No default distribution package root is available. Provide distributionPackagesRoot explicitly."
      };
    }

    if (!File.Exists(resolvedRspakPath))
    {
      return new
      {
        installed = false,
        error = "The .rspak file was not found.",
        rspakPath = resolvedRspakPath,
        distributionPackagesRoot = root
      };
    }

    try
    {
      using var archive = ZipFile.OpenRead(resolvedRspakPath);
      var topLevelRoots = archive.Entries
          .Select(entry => GetTopLevelSegment(entry.FullName))
          .Where(segment => !string.IsNullOrWhiteSpace(segment))
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .ToArray();

      if (topLevelRoots.Length != 1)
      {
        return new
        {
          installed = false,
          error = "The .rspak structure is invalid: expected exactly one top-level package root.",
          topLevelRoots,
          rspakPath = resolvedRspakPath
        };
      }

      var packageRootDirName = topLevelRoots[0];
      var targetPackageDir = Path.Combine(root, packageRootDirName);
      var hasManifest = archive.Entries.Any(entry => string.Equals(entry.FullName, packageRootDirName + "/manifest.xml", StringComparison.OrdinalIgnoreCase));
      var hasRobotStudioPayload = archive.Entries.Any(entry => entry.FullName.StartsWith(packageRootDirName + "/RobotStudio/", StringComparison.OrdinalIgnoreCase));

      if (Directory.Exists(targetPackageDir) && !overwriteExisting)
      {
        return new
        {
          installed = false,
          alreadyExists = true,
          error = "Package directory already exists and overwriteExisting=false.",
          packageRootDirName,
          targetPackageDir,
          rspakPath = resolvedRspakPath
        };
      }

      if (dryRun)
      {
        return new
        {
          installed = false,
          dryRun = true,
          packageRootDirName,
          targetPackageDir,
          rspakPath = resolvedRspakPath,
          overwriteExisting,
          hasManifest,
          hasRobotStudioPayload,
          entryCount = archive.Entries.Count
        };
      }

      Directory.CreateDirectory(root);
      if (Directory.Exists(targetPackageDir))
      {
        Directory.Delete(targetPackageDir, recursive: true);
      }

      ZipFile.ExtractToDirectory(resolvedRspakPath, root);

      return new
      {
        installed = true,
        packageRootDirName,
        targetPackageDir,
        rspakPath = resolvedRspakPath,
        overwriteExisting,
        hasManifest,
        hasRobotStudioPayload,
        entryCount = archive.Entries.Count
      };
    }
    catch (Exception ex)
    {
      return new
      {
        installed = false,
        error = ex.Message,
        rspakPath = resolvedRspakPath,
        distributionPackagesRoot = root
      };
    }
  }

  [McpServerTool(ReadOnly = true), Description("List RobotStudio package directories across default user-local distribution package roots (or optional overrides).")]
  public static object RspakList(
      [Description("Package ID/prefix to match (for example RobotStudioMCP). Ignored if packageDirectoryName is provided.")] string packageId = "RobotStudioMCP",
      [Description("Optional exact package directory name to list.")] string? packageDirectoryName = null,
      [Description("Optional override for primary RobotStudio distribution package root.")] string? distributionPackagesRoot = null,
      [Description("Optional override for secondary RobotStudio distribution package root.")] string? distributionPackagesRoot2 = null)
  {
    var roots = ResolveDistributionPackageRoots(distributionPackagesRoot, distributionPackagesRoot2).ToArray();

    var normalizedExact = (packageDirectoryName ?? string.Empty).Trim();
    var normalizedPrefix = (packageId ?? string.Empty).Trim();

    if (string.IsNullOrWhiteSpace(normalizedExact) && string.IsNullOrWhiteSpace(normalizedPrefix))
    {
      return new
      {
        listed = false,
        error = "Either packageDirectoryName or packageId must be provided."
      };
    }

    try
    {
      var packageEntries = new List<object>();

      foreach (var root in roots)
      {
        if (!Directory.Exists(root))
        {
          continue;
        }

        var matches = Directory.GetDirectories(root)
            .Where(path =>
            {
              var name = Path.GetFileName(path) ?? string.Empty;
              if (!string.IsNullOrWhiteSpace(normalizedExact))
              {
                return string.Equals(name, normalizedExact, StringComparison.OrdinalIgnoreCase);
              }

              return name.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var packagePath in matches)
        {
          var name = Path.GetFileName(packagePath) ?? string.Empty;
          var manifestPath = Path.Combine(packagePath, "manifest.xml");
          if (!File.Exists(manifestPath))
          {
            return new
            {
              listed = false,
              error = "Package cannot be listed because required manifest.xml is missing.",
              packageDirectoryName = name,
              packagePath,
              manifestPath,
              searchedRoots = roots
            };
          }

          packageEntries.Add(new
          {
            packageDirectoryName = name,
            packagePath,
            manifestPath,
            matchedRoot = root
          });
        }
      }

      return new
      {
        listed = true,
        searchedRoots = roots,
        packageId = normalizedPrefix,
        packageDirectoryName = normalizedExact,
        count = packageEntries.Count,
        packages = packageEntries
      };
    }
    catch (Exception ex)
    {
      return new
      {
        listed = false,
        error = $"Failed to list packages: {ex.Message}",
        searchedRoots = roots,
        packageId = normalizedPrefix,
        packageDirectoryName = normalizedExact
      };
    }
  }

  [McpServerTool(Destructive = true, Title = "Uninstall RobotStudio package"), Description("Uninstall a RobotStudio package directory from the distribution package root.")]
  public static object RspakUninstall(
      [Description("Package ID/prefix to match (for example RobotStudioMCP). Ignored if packageDirectoryName is provided.")] string packageId = "RobotStudioMCP",
      [Description("Optional exact package directory name (for example RobotStudioMCP-0.1.1) to remove.")] string? packageDirectoryName = null,
      [Description("Optional override for RobotStudio distribution package root. Default: %LOCALAPPDATA%\\ABB\\DistributionPackages2")] string? distributionPackagesRoot = null,
      [Description("If true, remove all matching package directories. If false and multiple matches exist, returns ambiguity error.")] bool removeAllMatches = false,
      [Description("If true, only report intended removals without writing changes.")] bool dryRun = false)
  {
    var root = string.IsNullOrWhiteSpace(distributionPackagesRoot)
        ? GetDefaultDistributionPackagesRoot()
        : distributionPackagesRoot.Trim();

    if (string.IsNullOrWhiteSpace(root))
    {
      return new
      {
        uninstalled = false,
        error = "No default distribution package root is available. Provide distributionPackagesRoot explicitly."
      };
    }

    if (!Directory.Exists(root))
    {
      return new
      {
        uninstalled = false,
        nothingToRemove = true,
        distributionPackagesRoot = root,
        message = "Distribution package root does not exist."
      };
    }

    var normalizedExact = (packageDirectoryName ?? string.Empty).Trim();
    var normalizedPrefix = (packageId ?? string.Empty).Trim();

    if (string.IsNullOrWhiteSpace(normalizedExact) && string.IsNullOrWhiteSpace(normalizedPrefix))
    {
      return new
      {
        uninstalled = false,
        error = "Either packageDirectoryName or packageId must be provided."
      };
    }

    string[] candidates;
    try
    {
      candidates = Directory.GetDirectories(root)
          .Where(path =>
          {
            var name = Path.GetFileName(path) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(normalizedExact))
            {
              return string.Equals(name, normalizedExact, StringComparison.OrdinalIgnoreCase);
            }

            return name.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase);
          })
          .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
          .ToArray();
    }
    catch (Exception ex)
    {
      return new
      {
        uninstalled = false,
        error = $"Failed to enumerate distribution package root: {ex.Message}",
        distributionPackagesRoot = root
      };
    }

    if (candidates.Length == 0)
    {
      return new
      {
        uninstalled = false,
        nothingToRemove = true,
        distributionPackagesRoot = root,
        packageId = normalizedPrefix,
        packageDirectoryName = normalizedExact
      };
    }

    if (candidates.Length > 1 && string.IsNullOrWhiteSpace(normalizedExact) && !removeAllMatches)
    {
      return new
      {
        uninstalled = false,
        ambiguous = true,
        error = "Multiple matching packages found. Provide packageDirectoryName or set removeAllMatches=true.",
        matches = candidates,
        distributionPackagesRoot = root,
        packageId = normalizedPrefix
      };
    }

    var toRemove = (candidates.Length <= 1 || removeAllMatches || !string.IsNullOrWhiteSpace(normalizedExact))
        ? candidates
        : Array.Empty<string>();

    if (dryRun)
    {
      return new
      {
        uninstalled = false,
        dryRun = true,
        distributionPackagesRoot = root,
        packageId = normalizedPrefix,
        packageDirectoryName = normalizedExact,
        removeAllMatches,
        matches = candidates,
        toRemove
      };
    }

    var removed = new List<string>();
    var errors = new List<string>();

    foreach (var path in toRemove)
    {
      try
      {
        Directory.Delete(path, recursive: true);
        removed.Add(path);
      }
      catch (Exception ex)
      {
        errors.Add($"Failed to remove '{path}': {ex.Message}");
      }
    }

    return new
    {
      uninstalled = errors.Count == 0,
      distributionPackagesRoot = root,
      packageId = normalizedPrefix,
      packageDirectoryName = normalizedExact,
      removeAllMatches,
      removed,
      errors
    };
  }

  internal static IEnumerable<string> ResolveDistributionPackageRoots(string? distributionPackagesRoot, string? distributionPackagesRoot2)
  {
    var overrides = new[] { distributionPackagesRoot, distributionPackagesRoot2 }
        .Select(root => (root ?? string.Empty).Trim())
        .Where(root => !string.IsNullOrWhiteSpace(root))
        .Select(root => root!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    if (overrides.Length > 0)
    {
      return overrides;
    }

    return new[]
    {
      DefaultUserDistributionPackagesRoot,
      DefaultUserDistributionPackagesRoot2
    }
    .Where(root => !string.IsNullOrWhiteSpace(root))
    .Select(root => root!)
    .Distinct(StringComparer.OrdinalIgnoreCase);
  }

  private static string GetTopLevelSegment(string fullName)
  {
    if (string.IsNullOrWhiteSpace(fullName)) return string.Empty;

    var normalized = fullName.Replace('\\', '/').Trim('/');
    if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

    var index = normalized.IndexOf('/');
    return index < 0 ? normalized : normalized[..index];
  }
}
