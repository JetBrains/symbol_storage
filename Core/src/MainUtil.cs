﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using JetBrains.Annotations;
using JetBrains.SymbolStorage.Impl;
using JetBrains.SymbolStorage.Impl.Commands;
using JetBrains.SymbolStorage.Impl.Logger;
using JetBrains.SymbolStorage.Impl.Storages;
using JetBrains.SymbolStorage.Impl.Tags;
using Microsoft.Extensions.CommandLineUtils;

namespace JetBrains.SymbolStorage
{
  public static class MainUtil
  {
    public enum MainMode
    {
      Full,
      UploadOnly
    }

    // Bug: Unix has one byte for exit code instead of Windows!!!
    //
    // Standard posix exit code meaning:
    //     1   - Catchall for general errors
    //     2   - Misuse of shell builtins (according to Bash documentation)
    //   126   - Command invoked cannot execute
    //   127   - “command not found”
    //   128   - Invalid argument to exit
    //   128+n - Fatal error signal “n”
    //   130   - Script terminated by Control-C
    //   255\* - Exit status out of range
    public static byte Main(Assembly mainAssembly, [NotNull] string[] args, MainMode mode)
    {
      try
      {
        var assemblyName = mainAssembly.GetName();
        var toolName = assemblyName.Name;
        var toolVersion = assemblyName.Version!.ToString(3);
        var commandLine = new CommandLineApplication
          {
            FullName = toolName
          };
        commandLine.HelpOption("-h|--help");
        commandLine.VersionOption("--version", () => toolVersion);

        var dirOption = commandLine.Option("-d|--directory", "The local directory with symbol server storage.", CommandOptionType.SingleValue);
        var awsS3BucketNameOption = commandLine.Option("-a|--aws-s3", $"The AWS S3 bucket with symbol server storage. The access and private keys will be asked in console. Use {AccessUtil.AwsS3AccessKeyEnvironmentVariable}, {AccessUtil.AwsS3SecretKeyEnvironmentVariable} and {AccessUtil.AwsCloudFrontDistributionIdEnvironmentVariable} environment variables for unattended mode.", CommandOptionType.SingleValue);
        var awsS3RegionEndpointOption = commandLine.Option("-ar|--aws-s3-region", $"The AWS S3 region endpoint with symbol server storage. Default is {AccessUtil.DefaultAwsS3RegionEndpoint}.", CommandOptionType.SingleValue);

        if (mode == MainMode.Full)
        {
          commandLine.Command("validate", x =>
            {
              x.HelpOption("-h|--help");
              x.Description = "Storage inconsistency check and fix known issues by request";
              var aclOption = x.Option("-r|--rights", "Validate access rights.", CommandOptionType.NoValue);
              var fixOption = x.Option("-f|--fix", "Fix known issues if possible.", CommandOptionType.NoValue);
              x.OnExecute(() => new ValidateCommand(
                ConsoleLogger.Instance,
                AccessUtil.GetStorage(dirOption.Value(), awsS3BucketNameOption.Value(), awsS3RegionEndpointOption.Value()),
                aclOption.HasValue(),
                fixOption.HasValue()).ExecuteAsync());
            });

          static void FilterOptions(
            CommandLineApplication x,
            out CommandOption incFilterProductOption,
            out CommandOption excFilterProductOption,
            out CommandOption incFilterVersionOption,
            out CommandOption excFilterVersionOption,
            out CommandOption safetyPeriodOption)
          {
            incFilterProductOption = x.Option("-fpi|--product-include-filter", "Select wildcard for include product filtering.", CommandOptionType.MultipleValue);
            excFilterProductOption = x.Option("-fpe|--product-exclude-filter", "Select wildcard for exclude product filtering.", CommandOptionType.MultipleValue);
            incFilterVersionOption = x.Option("-fvi|--version-include-filter", "Select wildcard for include version filtering.", CommandOptionType.MultipleValue);
            excFilterVersionOption = x.Option("-fve|--version-exclude-filter", "Select wildcard for exclude version filtering.", CommandOptionType.MultipleValue);
            safetyPeriodOption = x.Option("-sp|--safety-period", $"The safety period for young files. {AccessUtil.DefaultSafetyPeriod.Days:D} days by default.", CommandOptionType.MultipleValue);
          }

          commandLine.Command("list", x =>
            {
              x.HelpOption("-h|--help");
              x.Description = "List storage metadata information";
              FilterOptions(x,
                out var incFilterProductOption,
                out var excFilterProductOption,
                out var incFilterVersionOption,
                out var excFilterVersionOption,
                out var safetyPeriodOption);
              x.OnExecute(() => new ListCommand(
                ConsoleLogger.Instance,
                AccessUtil.GetStorage(dirOption.Value(), awsS3BucketNameOption.Value(), awsS3RegionEndpointOption.Value()),
                incFilterProductOption.Values,
                excFilterProductOption.Values,
                incFilterVersionOption.Values,
                excFilterVersionOption.Values,
                ParseDays(safetyPeriodOption.Value(), AccessUtil.DefaultSafetyPeriod)).ExecuteAsync());
            });

          commandLine.Command("delete", x =>
            {
              x.HelpOption("-h|--help");
              x.Description = "Delete storage metadata and referenced data files";
              FilterOptions(x,
                out var incFilterProductOption,
                out var excFilterProductOption,
                out var incFilterVersionOption,
                out var excFilterVersionOption,
                out var safetyPeriodOption);
              x.OnExecute(() => new DeleteCommand(
                ConsoleLogger.Instance,
                AccessUtil.GetStorage(dirOption.Value(), awsS3BucketNameOption.Value(), awsS3RegionEndpointOption.Value()),
                incFilterProductOption.Values,
                excFilterProductOption.Values,
                incFilterVersionOption.Values,
                excFilterVersionOption.Values,
                ParseDays(safetyPeriodOption.Value(), AccessUtil.DefaultSafetyPeriod)).ExecuteAsync());
            });
        }

        static void StorageOptions(
          CommandLineApplication x,
          out CommandOption newStorageFormatOption)
        {
          newStorageFormatOption = x.Option("-nsf|--new-storage-format", $"Select data files format for a new storage: {AccessUtil.NormalStorageFormat} (default), {AccessUtil.LowerStorageFormat}, {AccessUtil.UpperStorageFormat}.", CommandOptionType.SingleValue);
        }

        commandLine.Command("new", x =>
          {
            x.HelpOption("-h|--help");
            x.Description = "Create empty storage";
            StorageOptions(x, out var newStorageFormatOption);
            x.OnExecute(() => new NewCommand(
              ConsoleLogger.Instance,
              AccessUtil.GetStorage(dirOption.Value(), awsS3BucketNameOption.Value(), awsS3RegionEndpointOption.Value()),
              AccessUtil.GetStorageFormat(newStorageFormatOption.Value())).ExecuteAsync());
          });

        commandLine.Command("upload", x =>
          {
            x.HelpOption("-h|--help");
            x.Description = "Upload one storage to another one with the source storage inconsistency check";
            var sourceOption = x.Option("-s|--source", "Source storage directory.", CommandOptionType.SingleValue);
            StorageOptions(x, out var newStorageFormatOption);
            x.OnExecute(() => new UploadCommand(
              ConsoleLogger.Instance,
              AccessUtil.GetStorage(dirOption.Value(), awsS3BucketNameOption.Value(), awsS3RegionEndpointOption.Value()),
              sourceOption.Value(),
              AccessUtil.GetStorageFormat(newStorageFormatOption.Value())).ExecuteAsync());
          });

        commandLine.Command("create", x =>
          {
            x.HelpOption("-h|--help");
            x.Description = "Create temporary storage and upload it to another one";
            var compressWPdbOption = x.Option("-cwpdb|--compress-windows-pdb", "Enable compression for Windows PDB files. Windows only. Incompatible with the SSQP.", CommandOptionType.NoValue);
            var compressPeOption = x.Option("-cpe|--compress-pe", "Enable compression for PE files. Windows only. Incompatible with the SSQP.", CommandOptionType.NoValue);
            var keepNonCompressedOption = x.Option("-k|--keep-non-compressed", "Store also non-compressed version in storage.", CommandOptionType.NoValue);
            var propertiesOption = x.Option("-p|--property", "The property to be stored in metadata in following format: <key1>=<value1>[,<key2>=<value2>[,...]]. Can be declared many times.", CommandOptionType.MultipleValue);
            StorageOptions(x, out var newStorageFormatOption);
            var productArgument = x.Argument("product", "The product name.");
            var versionArgument = x.Argument("version", "The product version.");
            var sourcesOption = x.Argument("path [path [...]] or @file", "Source directories or files with symbols, executables and shared libraries.", true);
            x.OnExecute(async () =>
              {
                var storage = AccessUtil.GetStorage(dirOption.Value(), awsS3BucketNameOption.Value(), awsS3RegionEndpointOption.Value());
                var newStorageFormat = AccessUtil.GetStorageFormat(newStorageFormatOption.Value());
                var sources = await ParsePaths(sourcesOption.Values);
                var properties = propertiesOption.Values.ParseProperties();
                var tempDir = Path.Combine(Path.GetTempPath(), "storage_" + Guid.NewGuid().ToString("D"));
                try
                {
                  var res = await new CreateCommand(
                    ConsoleLogger.Instance,
                    new FileSystemStorage(tempDir),
                    StorageFormat.Normal,
                    toolName + '/' + toolVersion,
                    productArgument.Value,
                    versionArgument.Value,
                    compressPeOption.HasValue(),
                    compressWPdbOption.HasValue(),
                    keepNonCompressedOption.HasValue(),
                    properties,
                    sources).ExecuteAsync();
                  if (res != 0)
                    return res;

                  return await new UploadCommand(
                    ConsoleLogger.Instance,
                    storage,
                    tempDir,
                    newStorageFormat).ExecuteAsync();
                }
                finally
                {
                  Directory.Delete(tempDir, true);
                }
              });
          });

        if (args.Length != 0)
        {
          var res = commandLine.Execute(args);
          if (0 <= res && res < 126)
            return (byte) res;
          return 255;
        }

        commandLine.ShowHint();
        return 127;
      }
      catch (Exception e)
      {
        ConsoleLogger.Instance.Error(e.ToString());
        return 126;
      }
    }

    private static TimeSpan ParseDays([CanBeNull] string days, TimeSpan defaultDays)
    {
      return days != null ? TimeSpan.FromDays(ulong.Parse(days)) : defaultDays;
    }

    private static async Task<IReadOnlyCollection<string>> ParsePaths([NotNull] IEnumerable<string> paths)
    {
      if (paths == null)
        throw new ArgumentNullException(nameof(paths));
      var res = new List<string>();
      foreach (var path in paths)
        if (path.StartsWith('@'))
        {
          using var reader = new StreamReader(path.Substring(1));
          string line;
          while ((line = await reader.ReadLineAsync()) != null)
            if (line.Length != 0)
              res.Add(line);
        }
        else
          res.Add(path);

      return res;
    }
  }
}