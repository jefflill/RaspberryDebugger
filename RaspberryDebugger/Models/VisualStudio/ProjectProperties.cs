﻿//-----------------------------------------------------------------------------
// FILE:	    ProjectProperties.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:   Copyright (c) 2021 by neonFORGE, LLC.  All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics.Contracts;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Debug;
using Microsoft.VisualStudio.ProjectSystem.Properties;

using Neon.Common;
using Neon.Net;

using Newtonsoft.Json.Linq;

using Task = System.Threading.Tasks.Task;
using Microsoft.VisualStudio.Threading;

namespace RaspberryDebugger
{
    /// <summary>
    /// The Visual Studio <see cref="Project"/> class properties can only be
    /// accessed from the UI thread, so we'll use this class to capture the
    /// properties we need on a UI thread so we can use them later on other
    /// threads.
    /// </summary>
    internal class ProjectProperties
    {
        //--------------------------------------------------------------------
        // Static members

        /// <summary>
        /// Creates a <see cref="ProjectProperties"/> instance holding the
        /// necessary properties from a <see cref="Project"/>.  This must
        /// be called on a UI thread.
        /// </summary>
        /// <param name="solution">The current solution.</param>
        /// <param name="project">The source project.</param>
        /// <returns>The cloned <see cref="ProjectProperties"/>.</returns>
        public static ProjectProperties CopyFrom(Solution solution, Project project)
        {
            Covenant.Requires<ArgumentNullException>(solution != null, nameof(solution));
            Covenant.Requires<ArgumentNullException>(project != null, nameof(project));

            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrEmpty(project.FullName))
            {
                // We'll see this for unsupported Visual Studio projects and will just
                // return project properties indicating this.

                return new ProjectProperties()
                {
                    Name                  = project.Name,
                    FullPath              = project.FullName,
                    Framework             = string.Empty,
                    Configuration         = null,
                    IsNetCore             = false,
                    SdkVersion            = null,
                    OutputFolder          = null,
                    OutputFileName        = null,
                    IsExecutable          = false,
                    AssemblyName          = null,
                    DebugEnabled          = false,
                    DebugConnectionName   = null,
                    CommandLineArgs       = new List<string>(),
                    EnvironmentVariables  = new Dictionary<string, string>(),
                    IsSupportedSdkVersion = false,
                    IsRaspberryCompatible = false,
                    IsAspNet              = false,
                    AspPort               = 0,
                    AspLaunchBrowser      = false,
                    AspRelativeBrowserUri = null
                };
            }

            var projectFolder = Path.GetDirectoryName(project.FullName);
            var isNetCore     = true;
            var netVersion    = (SemanticVersion)null;
            var sdkName       = (string)null;

            // Read the properties we care about from the project.

            var targetFrameworkMonikers = (string)project.Properties.Item("TargetFrameworkMoniker").Value;
            var targetFramework = string.Empty;

            if (!(project is IVsBrowseObjectContext context))
            {
                context = project.Object as IVsBrowseObjectContext;
            }

            var frameworkService = context.UnconfiguredProject.Services.ExportProvider.GetExportedValueOrDefault<IActiveDebugFrameworkServices>();
            if (null != frameworkService)
            {
                ThreadHelper.JoinableTaskFactory.Run(async delegate
                {
                    var configuredProject = await frameworkService.GetConfiguredProjectForActiveFrameworkAsync().ConfigureAwait(false);
                    IProjectProperties properties = configuredProject.Services.ProjectPropertiesProvider.GetCommonProperties();

                    //targetFramework = configuredProject.ProjectConfiguration.Dimensions["TargetFramework"];
                    var successful = configuredProject.ProjectConfiguration.Dimensions.TryGetValue("TargetFramework", out targetFramework);

                    Log.Info($"configuredProject.ProjectConfiguration.Dimensions['TargetFramework']: {targetFramework}");

                });
            }

            var outputType              = (int)project.Properties.Item("OutputType").Value;

            var monikers = targetFrameworkMonikers.Split(',');

            isNetCore = monikers[0] == ".NETCoreApp";

            // Extract the version from the moniker.  This looks like: "Version=v5.0"

            var versionRegex = new Regex(@"(?<version>[0-9\.]+)$");

            netVersion = SemanticVersion.Parse(versionRegex.Match(monikers[1]).Groups["version"].Value);

            // The [dotnet --info] command doesn't work as I expected because it doesn't
            // appear to examine the project file when determining the SDK version.
            //
            //      https://github.com/nforgeio/RaspberryDebugger/issues/16
            //
            // So, we're just going to use the latest known SDK from our catalog instead.
            // This isn't ideal but should work fine for the vast majority of people.

            var targetSdk        = (Sdk)null;
            var targetSdkVersion = (SemanticVersion)null;

            foreach (var sdkItem in PackageHelper.SdkGoodCatalog.Items
                .Where(item => item.IsStandalone && item.Architecture == SdkArchitecture.ARM32))
            {
                var sdkVersion = SemanticVersion.Parse(sdkItem.Version);

                if (sdkVersion.Major != netVersion.Major || sdkVersion.Minor != netVersion.Minor)
                {
                    continue;
                }

                if (targetSdkVersion == null || sdkVersion > targetSdkVersion)
                {
                    targetSdkVersion = sdkVersion;
                    targetSdk        = new Sdk(sdkItem.Name, sdkItem.Version);;
                }
            }

            sdkName = targetSdk?.Name;

            // Load [Properties/launchSettings.json] if present to obtain the command line
            // arguments and environment variables as well as the target connection.  Note
            // that we're going to use the profile named for the project and ignore any others.
            //
            // The launch settings for Console vs. WebApps are a bit different.  WebApps include
            // a top-level "iisSettings"" property and two profiles: "IIS Express" and the
            // profile with the project name.  We're going to use the presence of the "iisSettings"
            // property to determine that we're dealing with a WebApp and we'll do some additional
            // processing based off of the project profile:
            //
            //      1. Launch the browser if [launchBrowser=true]
            //      2. Extract the site port number from [applicationUrl]
            //      3. Have the app listen on all IP addresses by adding this environment
            //         variable when we :
            //
            //              ASPNETCORE_SERVER.URLS=http://0.0.0.0:<port>

            var launchSettingsPath    = Path.Combine(projectFolder, "Properties", "launchSettings.json");
            var commandLineArgs       = new List<string>();
            var environmentVariables  = new Dictionary<string, string>();
            var isAspNet              = false;
            var aspPort               = 0;
            var aspLaunchBrowser      = false;
            var aspRelativeBrowserUri = "/";

            if (File.Exists(launchSettingsPath))
            {
                var settings = JObject.Parse(File.ReadAllText(launchSettingsPath));
                var profiles = settings.Property("profiles");

                if (profiles != null)
                {
                    foreach (var profile in ((JObject)profiles.Value).Properties())
                    {
                        if (profile.Name == project.Name)
                        {
                            var profileObject              = (JObject)profile.Value;
                            var environmentVariablesObject = (JObject)profileObject.Property("environmentVariables")?.Value;

                            commandLineArgs = ParseArgs((string)profileObject.Property("commandLineArgs")?.Value);

                            if (environmentVariablesObject != null)
                            {
                                foreach (var variable in environmentVariablesObject.Properties())
                                {
                                    environmentVariables[variable.Name] = (string)variable.Value;
                                }
                            }

                            // Extract additional settings for ASPNET projects.

                            if (settings.Property("iisSettings") != null)
                            {
                                isAspNet = true;

                                // Note that we're going to fall back to port 5000 if there are any
                                // issues parsing the application URL.

                                const int fallbackPort = 5000;

                                var jProperty = profileObject.Property("applicationUrl");

                                if (jProperty != null && jProperty.Value.Type == JTokenType.String)
                                {
                                    try
                                    {
                                        var uri = new Uri((string)jProperty.Value);

                                        aspPort = uri.Port;

                                        if (!NetHelper.IsValidPort(aspPort))
                                        {
                                            aspPort = fallbackPort;
                                        }
                                    }
                                    catch
                                    {
                                        aspPort = fallbackPort;
                                    }
                                }
                                else
                                {
                                    aspPort = fallbackPort;
                                }

                                jProperty = profileObject.Property("launchBrowser");

                                if (jProperty != null && jProperty.Value.Type == JTokenType.Boolean)
                                {
                                    aspLaunchBrowser = (bool)jProperty.Value;
                                }
                            }
                        }
                        else if (profile.Name == "IIS Express")
                        {
                            // For ASPNET apps, this profile may include a "launchUrl" which
                            // specifies the absolute or relative URI to display in a debug
                            // browser launched during debugging.
                            //
                            // We're going to normalize this as a relative URI and save it
                            // so we'll be able to launch the browser on the correct page.

                            var profileObject = (JObject)profile.Value;
                            var jProperty     = profileObject.Property("launchUrl");

                            if (jProperty != null && jProperty.Value.Type == JTokenType.String)
                            {
                                var launchUri = (string)jProperty.Value;

                                if (!string.IsNullOrEmpty(launchUri))
                                {
                                    try
                                    {
                                        var uri = new Uri(launchUri, UriKind.RelativeOrAbsolute);

                                        if (uri.IsAbsoluteUri)
                                        {
                                            aspRelativeBrowserUri = uri.PathAndQuery;
                                        }
                                        else
                                        {
                                            aspRelativeBrowserUri = launchUri;
                                        }
                                    }
                                    catch
                                    {
                                        // We'll fall back to "/" for any URI parsing errors.

                                        aspRelativeBrowserUri = "/";
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Get the target Raspberry from the debug settings.

            var projects            = PackageHelper.ReadRaspberryProjects(solution);
            var projectSettings     = projects[project.UniqueName];
            var debugEnabled        = projectSettings.EnableRemoteDebugging;
            var debugConnectionName = projectSettings.RemoteDebugTarget;

            // Determine whether the referenced .NET Core SDK is currently supported.

            var sdk = sdkName == null ? null : PackageHelper.SdkGoodCatalog.Items.SingleOrDefault(item => SemanticVersion.Parse(item.Name) == SemanticVersion.Parse(sdkName) && item.Architecture == SdkArchitecture.ARM32);

            var isSupportedSdkVersion = sdk != null;

            // Determine whether the project is Raspberry compatible.

            var isRaspberryCompatible = isNetCore &&
                                        outputType == 1 && // 1=EXE
                                        isSupportedSdkVersion;

            // We need to jump through some hoops to obtain the project GUID.

            var solutionService = RaspberryDebuggerPackage.Instance.SolutionService;

            Covenant.Assert(solutionService.GetProjectOfUniqueName(project.UniqueName, out var hierarchy) == VSConstants.S_OK);
            Covenant.Assert(solutionService.GetGuidOfProject(hierarchy, out var projectGuid) == VSConstants.S_OK);

            // Return the properties.

            return new ProjectProperties()
            {
                Name                  = project.Name,
                FullPath              = project.FullName,
                Framework             = targetFramework,
                Guid                  = projectGuid,
                Configuration         = project.ConfigurationManager.ActiveConfiguration.ConfigurationName,
                IsNetCore             = isNetCore,
                SdkVersion            = sdk?.Version,
                OutputFolder          = Path.Combine(projectFolder, project.ConfigurationManager.ActiveConfiguration.Properties.Item("OutputPath").Value.ToString()),
                OutputFileName        = (string)project.Properties.Item("OutputFileName").Value,
                IsExecutable          = outputType == 1,     // 1=EXE
                AssemblyName          = project.Properties.Item("AssemblyName").Value.ToString(),
                DebugEnabled          = debugEnabled,
                DebugConnectionName   = debugConnectionName,
                CommandLineArgs       = commandLineArgs,
                EnvironmentVariables  = environmentVariables,
                IsSupportedSdkVersion = isSupportedSdkVersion,
                IsRaspberryCompatible = isRaspberryCompatible,
                IsAspNet              = isAspNet,
                AspPort               = aspPort,
                AspLaunchBrowser      = aspLaunchBrowser,
                AspRelativeBrowserUri = aspRelativeBrowserUri
            };
        }

        /// <summary>
        /// Parses command line arguments from a string, trying to handle things like
        /// double and single quotes as well as escaped characters.
        /// </summary>
        /// <param name="commandLine">The source command line or <c>null</c>.</param>
        /// <returns>The list of parsed arguments.</returns>
        private static List<string> ParseArgs(string commandLine)
        {
            commandLine = commandLine ?? string.Empty;
            commandLine = commandLine.Trim();

            var args = new List<string>();

            if (string.IsNullOrWhiteSpace(commandLine))
            {
                return args;
            }

            var pos = 0;

            while (pos < commandLine.Length)
            {
                // Skip past any whitespace.

                if (char.IsWhiteSpace(commandLine[pos]))
                {
                    while (pos < commandLine.Length && char.IsWhiteSpace(commandLine[pos]))
                    {
                        pos++;
                    }

                    continue;
                }

                var arg = string.Empty;

                switch (commandLine[pos])
                {
                    case '\'':

                        // Single quoted string argument: scan for the terminating single quote.

                        pos++;

                        while (pos < commandLine.Length && commandLine[pos] != '\'')
                        {
                            if (commandLine[pos] == '\\')
                            {
                                // Escaped character

                                arg += commandLine[pos++];

                                if (pos >= commandLine.Length)
                                {
                                    throw new ArgumentException($"Invalid escape in: [{commandLine}]");
                                }

                                arg += commandLine[pos++];
                            }
                            else
                            {
                                arg += commandLine[pos++];
                            }
                        }

                        pos++;
                        break;

                    case '"':

                        // Double quoted string argument: scan for the terminating double quote.

                        pos++;

                        while (pos < commandLine.Length && commandLine[pos] != '"')
                        {
                            if (commandLine[pos] == '\\')
                            {
                                // Escaped character

                                arg += commandLine[pos++];

                                if (pos >= commandLine.Length)
                                {
                                    throw new ArgumentException($"Invalid escape in: [{commandLine}]");
                                }

                                arg += commandLine[pos++];
                            }
                            else
                            {
                                arg += commandLine[pos++];
                            }
                        }

                        pos++;
                        break;

                    default:

                        // Space delimited argument: scan for the terminating whitespace.

                        while (pos < commandLine.Length && !char.IsWhiteSpace(commandLine[pos]))
                        {
                            arg += commandLine[pos++];
                        }

                        pos++;
                        break;
                }

                if (arg.Length > 0)
                {
                    args.Add(Regex.Unescape(arg));
                }
            }

            return args;
        }

        //--------------------------------------------------------------------
        // Instance members

        /// <summary>
        /// Returns the project's friendly name.
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Returns the fullly qualfied path to the project file.
        /// </summary>
        public string FullPath { get; private set; }

        /// <summary>
        /// Returns the project's GUID.
        /// </summary>
        public Guid Guid { get; private set; }

        /// <summary>
        /// Indicates that the project targets .NET Core rather than the .NET Framework.
        /// </summary>
        public bool IsNetCore { get; private set; }

        /// <summary>
        /// Returns the projects .NET Core SDK version.  Note that this will probably include
        /// just the major and minor versions of the SDK.  This may also return <c>null</c>
        /// if the SDK version could not be identified.
        /// </summary>
        public string SdkVersion { get; private set; }

        /// <summary>
        /// Returns <c>true</c> if the project references a supported .NET Core SDK version.
        /// </summary>
        public bool IsSupportedSdkVersion { get; private set; }

        /// <summary>
        /// Returns the project's build configuration.
        /// </summary>
        public string Configuration { get; private set; }

        /// <summary>
        /// Returns the fully qualified path to the project's output directory.
        /// </summary>
        public string OutputFolder { get; private set; }

        /// <summary>
        /// Indicates that the program is an execuable as opposed to something
        /// else, like a DLL.
        /// </summary>
        public bool IsExecutable { get; private set; }

        /// <summary>
        /// Returns the publish runtime.
        /// </summary>
        public string Runtime => "linux-arm";

        /// <summary>
        /// Returns the framework version.
        /// </summary>
        public string Framework { get; private set; }

        /// <summary>
        /// Returns the publication folder.
        /// </summary>
        public string PublishFolder => Path.Combine(OutputFolder, Runtime);

        /// <summary>
        /// Returns the name of the output assembly.
        /// </summary>
        public string AssemblyName { get; private set; }

        /// <summary>
        /// Returns the name of the output binary file.
        /// </summary>
        public string OutputFileName { get; private set; }

        /// <summary>
        /// Indicates whether Raspberry debugging is enabled for this project.
        /// </summary>
        public bool DebugEnabled { get; private set; }

        /// <summary>
        /// Returns the connection name identifying the target Raspberry or <c>null</c>
        /// when the default Raspberry connection should be used.
        /// </summary>
        public string DebugConnectionName { get; private set; }

        /// <summary>
        /// Returns the command line arguments to be passed to the debugged program.
        /// </summary>
        public List<string> CommandLineArgs { get; private set; }

        /// <summary>
        /// Returns the environment variables to be passed to the debugged program.
        /// </summary>
        public Dictionary<string, string> EnvironmentVariables { get; private set; }

        /// <summary>
        /// Indicates whether the project is capable of being deployed and debugged
        /// on a Raspberry.
        /// </summary>
        public bool IsRaspberryCompatible { get; private set; }

        /// <summary>
        /// Indicates that this is an ASPNET project.
        /// </summary>
        public bool IsAspNet { get; private set; }

        /// <summary>
        /// Returns the port number to expose for the ASPNET apps.
        /// </summary>
        public int AspPort { get; private set; }

        /// <summary>
        /// Indicates whether a browser should be launched for ASPNET apps.
        /// </summary>
        public bool AspLaunchBrowser { get; private set; }

        /// <summary>
        /// Returns the relative URI to be displayed in the browser when
        /// <see cref="AspLaunchBrowser"/> is <c>true</c>.
        /// </summary>
        public string AspRelativeBrowserUri { get; private set; }
    }
}
