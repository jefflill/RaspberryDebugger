﻿//-----------------------------------------------------------------------------
// FILE:	    SettingsCommand.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:   Open Source
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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

using EnvDTE;
using EnvDTE80;

using Neon.Common;
using Neon.IO;
using Neon.Windows;

using Newtonsoft.Json.Linq;

using Task = System.Threading.Tasks.Task;

namespace RaspberryDebugger
{
    /// <summary>
    /// Handles the <b>Project/Raspberry Debug Settings...</b> command.
    /// </summary>
    internal sealed class SettingsCommand
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 0x0101;

        /// <summary>
        /// Package command set ID.
        /// </summary>
        public static readonly Guid CommandSet = RaspberryDebugPackage.CommandSet;

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly AsyncPackage package;

        private DTE2    dte;

        /// <summary>
        /// Initializes a new instance of the <see cref="SettingsCommand"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private SettingsCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            Covenant.Requires<ArgumentNullException>(package != null, nameof(package));
            Covenant.Requires<ArgumentNullException>(commandService != null, nameof(commandService));
            
            ThreadHelper.ThrowIfNotOnUIThread();

            this.package = package;
            this.dte     = (DTE2)Package.GetGlobalService(typeof(SDTE));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem      = new MenuCommand(this.Execute, menuCommandID);
             
            commandService.AddCommand(menuItem);
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static SettingsCommand Instance { get; private set; }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider => this.package;

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;

            SettingsCommand.Instance = new SettingsCommand(package, commandService);
        }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
#pragma warning disable VSTHRD100
        private async void Execute(object sender, EventArgs e)
#pragma warning restore VSTHRD100 
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (dte.Solution == null)
            {
                return;
            }

            var project = PackageHelper.GetStartupProject(dte.Solution);

            if (project == null)
            {
                return;
            }

            var targetFrameworkMonikers = (string)project.Properties.Item("TargetFrameworkMoniker").Value;
            var outputType              = (int)project.Properties.Item("OutputType").Value;
            var monikers                = targetFrameworkMonikers.Split(',');
            var isNetCore               = monikers[0] == ".NETCoreApp";
            var sdkVersion              = monikers[1].StartsWith("Version=v") ? monikers[1].Substring("Version=v".Length) : null;

            if (!isNetCore ||
                outputType != 1 /* EXE */ ||
                sdkVersion == null ||
                SemanticVersion.Parse(sdkVersion) < SemanticVersion.Parse("3.1"))
            {
                MessageBoxEx.Show(
                    "Raspberry debugging is not supported by this project type.  Only .NET Core applications targeting .NET Core 3.1 or greater are supported.",
                    "Unsupported Project Type",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            var raspberryProjects = PackageHelper.ReadRaspberryProjects(dte.Solution);
            var projectSettings   = raspberryProjects[project.UniqueName];
            var settingsDialog    = new SettingsDialog(projectSettings);

            if (settingsDialog.ShowDialog() == DialogResult.OK)
            {
                PackageHelper.WriteRaspberryProjects(dte.Solution, raspberryProjects);
            }
        }
    }
}
