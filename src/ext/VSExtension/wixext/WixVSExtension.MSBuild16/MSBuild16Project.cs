// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace Microsoft.Tools.WindowsInstallerXml.Extensions.WixVSExtension
{
	using System;
	using System.Diagnostics;
	using System.Collections;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Text.RegularExpressions;
	using System.Linq;
	using System.Xml;
	using System.Xml.Linq;
	using System.IO;
	using Microsoft.Build.Utilities;
	using Microsoft.Build.Evaluation;
	using Microsoft.Build.Execution;
	using Microsoft.Build.Framework;
	using Microsoft.Tools.WindowsInstallerXml.Extensions;

	public class MSBuild16Project : VSProjectHarvester.MSBuildProject
	{
		private BuildManager buildManager;
		private BuildParameters buildParameters;
		private Project currentProject;
		private ProjectInstance currentProjectInstance;
		private ProjectCollection projectCollection;
		private HarvesterCore     harvesterCore;
		private string configuration;
		private string platform;

		public MSBuild16Project(HarvesterCore harvesterCore, string configuration, string platform)
			: base(null, null, null, null)
		{

			this.harvesterCore = harvesterCore;
			this.configuration = configuration;
			this.platform = platform;
		}

		public override bool Build(string projectFileName, string[] targetNames, IDictionary targetOutputs)
		{
			try {
				this.buildManager.BeginBuild(this.buildParameters);

				BuildRequestData buildRequestData = new BuildRequestData(this.currentProjectInstance, targetNames, null, BuildRequestDataFlags.ReplaceExistingProjectInstance);

				BuildSubmission submission  = this.buildManager.PendBuildRequest(buildRequestData);

				BuildResult buildResult = submission.Execute();

				bool buildSucceeded = buildResult.OverallResult == BuildResultCode.Success;

				this.buildManager.EndBuild();

				// Fill in empty lists for each target so that heat will look at the item group later.
				foreach(string target in targetNames) {
					targetOutputs.Add(target, new List<object>());
				}

				return buildSucceeded;
			}
			catch(Exception e) {
				throw new WixException(VSErrors.CannotBuildProject(projectFileName, e.Message));
			}
		}

		public override VSProjectHarvester.MSBuildProjectItemType GetBuildItem(object buildItem)
		{
			return new MSBuild16ProjectItemType((ProjectItemInstance)buildItem);
		}

		public override IEnumerable GetEvaluatedItemsByName(string itemName)
		{
			return this.currentProjectInstance.GetItems(itemName);
		}

		public override string GetEvaluatedProperty(string propertyName)
		{
			return this.currentProjectInstance.GetPropertyValue(propertyName);
		}

		public override void Load(string projectPath)
		{
			var doc      = XDocument.Load(projectPath);
			var sdkStyle = doc.Root.Attributes("Sdk").Any();
			var globalVariables = new Dictionary<string, string>();
			var basePath = GetCoreBasePath(projectPath);
			if(sdkStyle) {
				var tfm = doc.Root.Descendants("TargetFramework").FirstOrDefault()?.Value ?? doc.Root.Descendants("TargetFrameworks").FirstOrDefault()?.Value.Split(';').FirstOrDefault() ?? throw new Exception("Could not find TargetFramework for project.");
				globalVariables = GetCoreGlobalProperties(projectPath, basePath, tfm);
				Environment.SetEnvironmentVariable("MSBuildExtensionsPath", globalVariables["MSBuildExtensionsPath"]);
				Environment.SetEnvironmentVariable("MSBuildSDKsPath", globalVariables["MSBuildSDKsPath"]);
				SetMsBuildExePath();
			}
			if(configuration != null || platform != null) {
				if(configuration != null) {
					globalVariables.Add("Configuration", configuration);
				}

				if(platform != null) {
					globalVariables.Add("Platform", platform);
				}

			}
			this.buildParameters = new BuildParameters();
			try {
				HarvestLogger logger = new HarvestLogger();
				logger.HarvesterCore = harvesterCore;
				List<ILogger> loggers = new List<ILogger>();
				loggers.Add(logger);

				this.buildParameters.Loggers = loggers;

				// MSBuild can't handle storing operating environments for nested builds.
				if(Util.RunningInMsBuild) {
					this.buildParameters.SaveOperatingEnvironment = false;
				}
			}
			catch(Exception e) {
				if(harvesterCore != null) {
					harvesterCore.OnMessage(VSWarnings.NoLogger(e.Message));
				}
			}

			try {
				this.buildManager = new BuildManager();
				this.projectCollection = new ProjectCollection(globalVariables);
				if(sdkStyle) {
					projectCollection.AddToolset(new Toolset(ToolLocationHelper.CurrentToolsVersion, basePath, projectCollection, string.Empty));
				}
				this.currentProject = this.projectCollection.LoadProject(projectPath);
				this.currentProjectInstance = this.currentProject.CreateProjectInstance();
			}
			catch(Exception e) {
				throw new WixException(VSErrors.CannotLoadProject(projectPath, e.Message));
			}
		}

		public string GetCoreBasePath(string projectPath)
		{
			// Ensure that we set the DOTNET_CLI_UI_LANGUAGE environment variable to "en-US" before
			// running 'dotnet --info'. Otherwise, we may get localized results.
			string originalCliLanguage = Environment.GetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE");
			Environment.SetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE", "en-US");

			try {
				// Create the process info
				ProcessStartInfo startInfo = new ProcessStartInfo("dotnet", "--info")
				{
					// global.json may change the version, so need to set working directory
					WorkingDirectory = Path.GetDirectoryName(projectPath),
					CreateNoWindow = true,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true
				};

				// Execute the process
				using(Process process = Process.Start(startInfo)) {
					List<string> lines = new List<string>();
					process.OutputDataReceived += (_, e) => {
						if(!string.IsNullOrWhiteSpace(e.Data)) {
							lines.Add(e.Data);
						}
					};
					process.BeginOutputReadLine();
					process.WaitForExit();
					return ParseCoreBasePath(lines);
				}
			}
			finally {
				Environment.SetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE", originalCliLanguage);
			}
		}

		public string ParseCoreBasePath(List<string> lines)
		{
			if(lines == null || lines.Count == 0) {
				throw new Exception("Could not get results from `dotnet --info` call");
			}

			foreach(string line in lines) {
				int colonIndex = line.IndexOf(':');
				if(colonIndex >= 0
					&& line.Substring(0, colonIndex).Trim().Equals("Base Path", StringComparison.OrdinalIgnoreCase)) {
					return line.Substring(colonIndex + 1).Trim();
				}
			}

			throw new Exception("Could not locate base path in `dotnet --info` results");
		}

		public Dictionary<string, string>
		GetCoreGlobalProperties(string projectPath, string toolsPath, string targetFramework)
		{
			string solutionDir = Path.GetDirectoryName(projectPath);
			string extensionsPath = toolsPath;
			string sdksPath = Path.Combine(toolsPath, "Sdks");
			string roslynTargetsPath = Path.Combine(toolsPath, "Roslyn");
			var result = new Dictionary<string, string>
			{
				{ "SolutionDir",           solutionDir },
				{ "MSBuildExtensionsPath", extensionsPath },
				{ "MSBuildSDKsPath",       sdksPath },
				{ "RoslynTargetsPath",     roslynTargetsPath },
			};

			var nugetAssemblyPath                          = Path.Combine(GetVsDirectory(), "Common7", "IDE", "CommonExtensions", "Microsoft", "NuGet", "NuGet.Frameworks.dll");
			var NuGetAssembly                              = System.Reflection.Assembly.LoadFile(nugetAssemblyPath);
			var NuGetFramework                             = NuGetAssembly.GetType("NuGet.Frameworks.NuGetFramework");
			var NuGetFrameworkCompatibilityProvider        = NuGetAssembly.GetType("NuGet.Frameworks.CompatibilityProvider");
			var NuGetFrameworkDefaultCompatibilityProvider = NuGetAssembly.GetType("NuGet.Frameworks.DefaultCompatibilityProvider");
			var ParseMethod                                = NuGetFramework.GetMethod("Parse", new Type[] { typeof(string) });
			var IsCompatibleMethod                         = NuGetFrameworkCompatibilityProvider.GetMethod("IsCompatible");
			var DefaultCompatibilityProvider               = NuGetFrameworkDefaultCompatibilityProvider.GetMethod("get_Instance").Invoke(null, new object[] { });
			var FrameworkProperty                          = NuGetFramework.GetProperty("Framework");
			var VersionProperty                            = NuGetFramework.GetProperty("Version");
			var PlatformProperty                           = NuGetFramework.GetProperty("Platform");
			var PlatformVersionProperty                    = NuGetFramework.GetProperty("PlatformVersion");
			object Parse(string tfm)
			{
				return ParseMethod.Invoke(null, new object[] { tfm });
			}

			string GetTargetFrameworkIdentifier(string tfm)
			{
				return FrameworkProperty.GetValue(Parse(tfm)) as string;
			}

			string GetTargetFrameworkVersion(string tfm, int minVersionPartCount = 2)
			{
				var version = VersionProperty.GetValue(Parse(tfm)) as Version;
				return GetNonZeroVersionParts(version, minVersionPartCount);
			}

			string GetTargetPlatformIdentifier(string tfm)
			{
				return PlatformProperty.GetValue(Parse(tfm)) as string;
			}

			string GetTargetPlatformVersion(string tfm, int minVersionPartCount)
			{
				var version = PlatformVersionProperty.GetValue(Parse(tfm)) as Version;
				return GetNonZeroVersionParts(version, minVersionPartCount);
			}

			/*
			bool IsCompatible(string target, string candidate)
			{
				return Convert.ToBoolean(IsCompatibleMethod.Invoke(DefaultCompatibilityProvider, new object[] { Parse(target), Parse(candidate) }));
			}
			*/

			string GetNonZeroVersionParts(Version version, int minVersionPartCount)
			{
				var nonZeroVersionParts = version.Revision == 0 ? version.Build == 0 ? version.Minor == 0 ? 1 : 2 : 3: 4;
				return version.ToString(Math.Max(nonZeroVersionParts, minVersionPartCount));
			}
			/*
			dotnet/sdk/5.0.100/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.TargetFrameworkInference.targets:52.

			<PropertyGroup Condition="'$(TargetFramework)' != '' and ('$(TargetFrameworkIdentifier)' == '' or '$(TargetFrameworkVersion)' == '')">
				<TargetFrameworkIdentifier>$([MSBuild]::GetTargetFrameworkIdentifier('$(TargetFramework)'))</TargetFrameworkIdentifier>
				<TargetFrameworkVersion>v$([MSBuild]::GetTargetFrameworkVersion('$(TargetFramework)', 2))</TargetFrameworkVersion>
			</PropertyGroup>
			*/
			var targetFrameworkIdentifier = GetTargetFrameworkIdentifier(targetFramework);
			var targetFrameworkVersion    = GetTargetFrameworkVersion(targetFramework, 2);
			result.Add("TargetFrameworkIdentifier", targetFrameworkIdentifier ?? "");
			result.Add("TargetFrameworkVersion",    $"v{targetFrameworkVersion}");
			/*
			dotnet/sdk/5.0.100/Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.TargetFrameworkInference.targets:60.

			<PropertyGroup Condition="'$(TargetFramework)' != '' and ('$(TargetPlatformIdentifier)' == '' or '$(TargetPlatformVersion)' == '')">
				<TargetPlatformIdentifier Condition="'$(TargetPlatformIdentifier)' == ''">$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)'))</TargetPlatformIdentifier>
				<TargetPlatformVersion Condition="'$(TargetPlatformIdentifier)' == 'Windows'" >$([MSBuild]::GetTargetPlatformVersion('$(TargetFramework)', 4))</TargetPlatformVersion>
				<TargetPlatformVersion Condition="'$(TargetPlatformVersion)' == '' or ('$(TargetPlatformIdentifier)' == 'Windows' and !$([MSBuild]::VersionGreaterThanOrEquals($(TargetPlatformVersion), 10.0)))" >$([MSBuild]::GetTargetPlatformVersion('$(TargetFramework)', 2))</TargetPlatformVersion>
				<TargetPlatformVersion Condition="$([MSBuild]::VersionEquals($(TargetPlatformVersion), 0.0))" ></TargetPlatformVersion>
				<!-- Normalize casing of windows to Windows -->
				<TargetPlatformIdentifier Condition="'$(TargetPlatformIdentifier)' == 'Windows'">Windows</TargetPlatformIdentifier>
			</PropertyGroup>

			 */

			var targetPlatformIdentifier = GetTargetPlatformIdentifier(targetFramework);
			var targetPlatformVersion    = default(string);
			if(targetPlatformIdentifier.Equals("Windows", StringComparison.OrdinalIgnoreCase)) {
				targetPlatformVersion = GetTargetPlatformVersion(targetFramework, 4);
			}
			if(targetPlatformVersion == "" || targetPlatformIdentifier.Equals("Windows", StringComparison.OrdinalIgnoreCase) && Version.Parse(targetPlatformVersion).Major < 10) {
				targetPlatformVersion = GetTargetPlatformVersion(targetFramework, 2);
			}
			if(targetPlatformVersion == "0.0") {
				targetPlatformVersion = "7.0"; // This fixes some problems with version resolution on C:\Program Files\dotnet\sdk\5.0.100\Microsoft.Common.CurrentVersion.targets (which should not be there, but are nonetheless)
			}
			if(targetPlatformIdentifier.Equals("Windows", StringComparison.OrdinalIgnoreCase)) {
				targetPlatformIdentifier = "Windows";
			}
			result.Add("TargetPlatformIdentifier", targetPlatformIdentifier ?? "");
			result.Add("TargetPlatformVersion",    targetPlatformVersion    ?? "");
			// System.Console.WriteLine($"TargetFrameworkIdentifier: '{targetFrameworkIdentifier}'");
			// System.Console.WriteLine($"TargetFrameworkVersion:    '{targetFrameworkVersion}'");
			// System.Console.WriteLine($"TargetPlatformIdentifier:  '{targetPlatformIdentifier is null}'");
			// System.Console.WriteLine($"TargetPlatformVersion:     '{targetPlatformVersion  is null}'");
			return result;
		}

		private static void
		SetMsBuildExePath()
		{
			// var startInfo = new ProcessStartInfo("dotnet", "--list-sdks")
			// {
			// 	RedirectStandardOutput = true,
			// 	UseShellExecute        = false,
			// };

			// var process = Process.Start(startInfo);
			// process.WaitForExit(1000);

			// var output = process.StandardOutput.ReadToEnd();
			// var sdkPaths = Regex.Matches(output, "([0-9]+.[0-9]+.[0-9]+) \\[(.*)\\]")
			// 	.OfType<Match>()
			// 	.Select(m => System.IO.Path.Combine(m.Groups[2].Value, m.Groups[1].Value, "MSBuild.dll"));

			// var sdkPath = sdkPaths.Last();
			if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MSBUILD_EXE_PATH"))) {
				var msbuildPath = Path.Combine(GetMsBuildDirectory(), "MSBuild.exe");
				Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", msbuildPath);
			}
		}

		static string GetVsDirectory()
		{
			var vsPath = Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Professional\");
			if(!Directory.Exists(vsPath)) {
				return vsPath.Replace("Professional", "BuildTools");
			}
			else {
				return vsPath;
			}
		}

		static string GetMsBuildDirectory()
		{
			return Path.Combine(GetVsDirectory(), @"MSBuild\Current\Bin\");
		}

		private class MSBuild16ProjectItemType : VSProjectHarvester.MSBuildProjectItemType
		{
			private ProjectItemInstance projectItemInstance;

			public MSBuild16ProjectItemType(ProjectItemInstance projectItemInstance)
				: base(projectItemInstance)
			{
				this.projectItemInstance = projectItemInstance;
			}

			public override string ToString()
			{
				return this.projectItemInstance.EvaluatedInclude;
			}

			public override string GetMetadata(string name)
			{
				return this.projectItemInstance.GetMetadataValue(name);
			}
		}

		// This logger will derive from the Microsoft.Build.Utilities.Logger class,
		// which provides it with getters and setters for Verbosity and Parameters,
		// and a default empty Shutdown() implementation.
		private class HarvestLogger : ILogger
		{
			public HarvesterCore HarvesterCore { get; set; }

			/// <summary>
			/// Initialize is guaranteed to be called by MSBuild at the start of the build
			/// before any events are raised.
			/// </summary>
			public void Initialize(IEventSource eventSource)
			{
				eventSource.ErrorRaised += new BuildErrorEventHandler(eventSource_ErrorRaised);
			}

			public void Shutdown()
			{
			}

			public LoggerVerbosity Verbosity { get; set; }
			public string Parameters { get; set; }

			void eventSource_ErrorRaised(object sender, BuildErrorEventArgs e)
			{
				if(this.HarvesterCore != null) {
					// BuildErrorEventArgs adds LineNumber, ColumnNumber, File, amongst other parameters.
					string line = String.Format(CultureInfo.InvariantCulture, "{0}({1},{2}): {3}", e.File, e.LineNumber, e.ColumnNumber, e.Message);
					this.HarvesterCore.OnMessage(VSErrors.BuildErrorDuringHarvesting(line));
				}
			}
		}
	}
}
